# nanostream
a fast unaligned bitstream serialization utility for C# and Zig

warning: only little-endian machines are supported! assert this once on startup for safety.

## usage
drop in the source file to your project


if you want struct ser/des interop, make sure the structs are the same byte layout:
- C#: `[StructLayout(LayoutKind.Sequential, Pack = 1)]`
- Zig: `extern struct` with `align(1)` on every field

note: struct interop is untested! nanostream is primarily for primitives and those are heavily tested.


### C#
```cs
using Nanostream;

enum Fruit : ushort { Apple = 67, Pear = 0xFF12 }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Thing
{
    public float f;
    public bool b;
    public ushort u;
}

void Example()
{
    // ser
    BitWriter w = new();
    w.Write(123);               // 32 bits
    w.Write(-0.1122300491f);    // 32 bits
    w.Write(9.24);              // 64 bits

    w.WriteString("goodbye, world"); // 2 + 14 bytes

    w.Write(620676107661034ul); // 64 bits
    w.Write((sbyte)-11);        // 8 bits

    w.Write(Fruit.Apple);       // 16 bits
    w.WriteStruct(new Thing { f = -10f, b = false, u = 5000 }); // 7 bytes
    w.Write(true);              // 1 bit

    byte[] data = w.Conclude();

    // des
    BitReader r = new(data);
    r.Read(out int i);          // 123
    r.Read(out float f);        // -0.1122300491f
    r.Read(out double d);       // 9.24

    r.ReadString(out string s); // "goodbye, world"

    r.Read(out ulong u);        // 620676107661034
    r.Read(out sbyte sb);       // -11

    r.Read(out Fruit fruit);    // Fruit.Apple
    r.ReadStruct(out Thing thing); // { f = -10f, b = false, u = 5000 }
    r.Read(out bool b);         // true
}
```

### Zig
```zig
const Fruit = enum(u16) { Apple = 67, Pear = 0xFF12 };
const Thing = extern struct {
    f: f32 align(1),
    b: bool align(1),
    u: u16 align(1),
};

pub fn example(alloc: std.mem.Allocator) !void {
    // ser
    var w = try BitWriter.init(alloc, 4);

    w.write(@as(i32, 123)); // 32 bits
    w.write(@as(f32, -0.1122300491)); // 32 bits
    w.write(@as(f64, 9.24)); // 64 bits

    w.writeString("goodbye, world"); // 2 + 14 bytes

    w.write(@as(u64, 620676107661034)); // 64 bits
    w.write(@as(i8, -11)); // 8 bits

    w.write(Fruit.Apple); // 16 bits
    w.write(Thing{ .f = -10, .b = false, .u = 5000 }); // 7 bytes
    w.write(true); // 1 bit

    const data = try w.conclude();
    defer alloc.free(data);

    // des
    var r = BitReader.init(data);

    const i = r.read(i32); // 123
    const f = r.read(f32); // -0.1122300491
    const d = r.read(f64); // 9.24

    const s = (try r.readString(alloc)).?; // "goodbye, world"
    defer alloc.free(s);

    const u = r.read(u64); // 620676107661034
    const sb = r.read(i8); // -11

    const fruit = r.read(Fruit); // Fruit.Apple
    const thing = r.read(Thing); // { .f = -10, .b = false, .u = 5000 }
    const b = r.read(bool); // true

    _ = .{ i, f, d, s, u, sb, fruit, thing, b };
}
```

## real-world examples

### game state rollback (auto-generated code)
AKA save/load system
```cs
public partial class Dialog : ISync
{
	public virtual void Ser(Nanostream.BitWriter w, SyncSerContext ctx)
	{
		w.Write(fetchTimer);
		w.Write((ushort)pickers.Count);
		foreach(var v in pickers)
		{
			if(v != null)
			{
				w.Write(v.GetComponent<NetBody>().netID);
			}
			else
			{
				w.Write(ushort.MaxValue);
			}
		}
		w.Write(timeSincePickerNear);
	}
	public virtual void Des(Nanostream.BitReader r, SyncDesContext ctx)
	{
		r.Read(out fetchTimer);
		r.Read(out ushort len);
		pickers.Clear();
		for(int i = 0; i < len; i++)
		{
			r.Read(out ushort netID);
			global::ItemPicker val = ((ushort)netID == ushort.MaxValue) ? null : NetBody.ByID(netID).GetComponent<global::ItemPicker>();
			pickers.Add(val);
		}
		r.Read(out timeSincePickerNear);
	}
}
```

### lobby packet protocol
client:
```cs
void OnServerMsg(byte[] msg, uint lobbyID)
{
    Nanostream.BitReader r = new(msg);
    r.Read(out ServerPacketID type); // 1-byte enum
    switch (type)
    {
        case ServerPacketID.OneFieldChanged:
        {
            r.Read(out Room.ConfigID cID);
            Lobby.Current.room.DesField(cID, r);
            break;
        }
        case ServerPacketID.OneCustomizationChanged:
        {
            r.Read(out Room.CustomizationID cID);
            r.Read(out PlayerID who);
            Lobby.Current.room.DesCustomization(cID, who, r);
            break;
        }
        case ServerPacketID.YouAreANewJoiner:
        {
            Lobby.Current = new(false);
            r.ReadString(out Lobby.Current.lobbyName);

            // read my pid
            r.Read(out PlayerID myAssignedPID);
            ...
        }
        ...
    }
}
```

server:
```zig
fn onClientMsg(from: PlayerID, packet: []u8, io: std.Io, lobby: *Lobby) !void {
    lobby.everything_mutex.lock(io) catch return error.ServerHasABug;
    defer lobby.everything_mutex.unlock(io);

    var r: BitReader = .init(packet);
    const packetType = r.read(ClientPacketID);

    switch (packetType) {
        .ImTryingToChangeASetting => {
            if (lobby.locked_in) return;
            if (!lobby.allow_everyone_to_edit and !lobby.is_host(from)) return error.ClientHasABug_ImTryingToChangeASetting_YouArentAllowed;

            const id = r.read(ConfigID);
            try client_trying_to_change_config(io, lobby, id, &r);
        },
        .ImTryingToChangeMyCustomization => {
            if (lobby.locked_in) return;

            const id = r.read(CustomizationID);
            try client_trying_to_change_customization(io, lobby, from, id, &r);
        },
        ...
    }
    ...
}
```