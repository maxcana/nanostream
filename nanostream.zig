const std = @import("std");

// nanostream: unaligned bit writer and reader utility

pub const BitWriter = struct {
    buf: std.ArrayListUnmanaged(u8),
    alloc: std.mem.Allocator,
    current: u64 = 0,
    /// number of bits written in `current`: 0–63
    bitIndex: u8 = 0,

    /// recommended `capacity` is 4.
    pub fn init(alloc: std.mem.Allocator, capacity: usize) error{OutOfMemory}!BitWriter {
        return .{
            .buf = try .initCapacity(alloc, capacity),
            .alloc = alloc,
        };
    }

    /// Writes bits and moves forward.
    ///
    /// - `stuff`: Expects LTR structure placed at the right like `0b00010100`
    /// - `bits`: Number of bits to write from `stuff` (RTL). Maximum `64`. I can't use comptime here, so I'm just trusting you DO NOT MAKE IT >64.
    ///
    /// Ex: `stuff`=`0b0000000000000000000000000000000000000000000000000000000011110100` with bits = `5` adds `10100` to the stream.
    fn writeBits(self: *BitWriter, stuff: u64, bits: u8) void {
        var stufff = stuff; // make it mut

        // clear the top bytes we arent writing
        if (bits < 64) {
            stufff &= (@as(u64, 1) << @intCast(bits)) - @as(u64, 1);
        }

        if (self.bitIndex + bits >= 64) {
            // write in 2 parts
            const bits_1: u6 = @truncate(64 - self.bitIndex);
            const bits_2: u6 = @truncate(bits - (64 - self.bitIndex));

            const stuff_1: u64 = stufff >> bits_2;
            self.current <<= bits_1;
            self.current |= stuff_1;
            // == ByteList.Add_u64 ==
            self.buf.ensureUnusedCapacity(self.alloc, 8) catch unreachable; // this will crash on OOM — feel free to do something different, but who wants to "try w.write" every time
            const dst: *[8]u8 = self.buf.unusedCapacitySlice()[0..8];
            std.mem.writeInt(u64, dst, self.current, .big);
            self.buf.items.len += 8;
            // ======================

            self.current = 0;
            if (bits_2 > 0) {
                const stuff_2 = stufff & ((@as(u64, 1) << bits_2) - @as(u64, 1));
                self.current <<= bits_2;
                self.current |= stuff_2;
            }
        } else {
            self.current <<= @intCast(bits);
            self.current |= stufff;
        }

        self.bitIndex += bits;
        self.bitIndex %= 64;
    }
    /// - bits: Finishes up the current byte. If this completes the u64 `current`, `WriteBits` automatically flushes the entire u64 it into the array.
    /// - bytes: In case we didn't finish the u64 `current`, flush it byte-by-byte anyway (not optimized, we don't write a whole u64).
    fn flushBitsAndBytes(self: *BitWriter) error{OutOfMemory}!void {
        if (self.bitIndex > 0) {
            // finish up current byte exactly
            if (self.bitIndex % 8 != 0)
                self.writeBits(0, 8 - (self.bitIndex % 8));

            // force push single bytes (if we added a u64 already: bitIndex == 0 -> skip)
            // bitIndex must be a multiple of 8 after WriteBits
            while (self.bitIndex > 0) {
                try self.buf.append(self.alloc, @as(u8, @truncate(self.current >> @intCast(self.bitIndex - 8))));
                self.bitIndex -= 8;
            }
        }
    }

    /// Finalization function.
    /// Force write the current byte, which isn't normally written until it reaches 8 bits. Call after writing all bits.
    ///
    /// Warning: you now own this slice I give you, free it later with the allocator you gave me! (unless you like leaking)
    pub fn conclude(self: *BitWriter) error{OutOfMemory}![]u8 {
        try self.flushBitsAndBytes();
        return self.buf.toOwnedSlice(self.alloc);
    }

    pub fn write(self: *BitWriter, n: anytype) void {
        switch (@typeInfo(@TypeOf(n))) {
            .int => |info| {
                if (info.signedness == .signed) {
                    writeBits(self, @bitCast(@as(i64, n)), info.bits);
                } else {
                    writeBits(self, @as(u64, n), info.bits);
                }
            },
            .float => |info| {
                switch (info.bits) {
                    16 => writeBits(self, @as(u16, @bitCast(@as(f16, n))), 16),
                    32 => writeBits(self, @as(u32, @bitCast(@as(f32, n))), 32),
                    64 => writeBits(self, @as(u64, @bitCast(@as(f64, n))), 64),

                    else => @compileError("I CANT WRITE TYPE '" ++ @typeName(@TypeOf(n)) ++ "'"),
                }
            },
            .bool => {
                writeBits(self, @intFromBool(n), 1);
            },
            .@"enum" => |info| {
                const backing_val = @intFromEnum(n);
                const backing_info = @typeInfo(info.tag_type).int;
                if (backing_info.signedness == .signed) {
                    self.writeBits(@bitCast(@as(i64, backing_val)), backing_info.bits);
                } else {
                    self.writeBits(@as(u64, backing_val), backing_info.bits);
                }
            },
            .@"struct" => |info| {
                if (info.layout == .auto) {
                    @compileError("struct '" ++ @typeName(@TypeOf(n)) ++ "' must be `extern` or `packed` for a stable wire layout");
                }
                self.writeByteArray(std.mem.asBytes(&n));
            },

            // ... (all iX, uX, fX, plus bool. the custom types like i27 works as well probably)
            else => {
                @compileError("I CANT WRITE TYPE '" ++ @typeName(@TypeOf(n)) ++ "'");
            },
        }
    }

    pub fn writeString(self: *BitWriter, str: []const u8) void {
        self.write(@as(u16, @intCast(str.len)));
        self.writeByteArray(str);
    }

    fn writeByteArray(self: *BitWriter, n: []const u8) void {
        var ptr = n;
        while (ptr.len >= 8) : (ptr = ptr[8..]) {
            self.writeBits(std.mem.readInt(u64, ptr[0..8], .big), 64);
        }
        while (ptr.len > 0) : (ptr = ptr[1..]) {
            self.writeBits(ptr[0], 8);
        }
    }
};

pub const BitReader = struct {
    buf: []const u8,
    byteIndex: u32 = 0,
    cache: u64 = 0,
    bitIndex: u8 = 0,

    pub fn init(buf: []const u8) BitReader {
        var r: BitReader = .{
            .buf = buf,
        };
        r.obtainCache();

        return r;
    }

    fn obtainCache(self: *BitReader) void {
        if (self.byteIndex + 8 <= self.buf.len) {
            self.cache = std.mem.readInt(u64, self.buf[self.byteIndex..][0..8], .big);
        } else {
            self.cache = 0;
            var idx: u32 = self.byteIndex;
            while (idx < self.buf.len) {
                self.cache <<= 8;
                self.cache |= self.buf[idx];
                idx += 1;
            }

            self.cache <<= @as(u6, @truncate(64 - 8 * (self.buf.len - self.byteIndex)));
        }
    }

    /// Reads bits and moves forward.
    ///
    /// - `bits`: Maximum `64`.
    ///
    /// Bits are placed in an output of the exact specified bit-width.
    /// Ex. ...`0000000`(You are here)`1100101`... with `bits`=`5` returns a `u5`: `0b11001`.
    fn readBits(self: *BitReader, comptime bits: u8) @Int(.unsigned, bits) {
        if (bits > 64) @compileError("bits can't be >64 buddy");
        if (bits == 0) return 0;

        var output: u64 = 0;

        if (self.bitIndex + bits >= 64) {
            const bits_1: u8 = @intCast(64 - self.bitIndex);
            const bits_2: u8 = @intCast(bits - bits_1);

            const reading_1 = self.cache >> @intCast(64 - bits_1);

            self.byteIndex += 8;
            self.obtainCache();

            if (bits_2 > 0) {
                const reading_2: u64 = self.cache >> @intCast(64 - bits_2);
                self.cache <<= @intCast(bits_2);
                output = (reading_1 << @intCast(bits_2)) | reading_2;
            } else {
                output = reading_1;
            }
        } else {
            output = self.cache >> @intCast(64 - bits);
            self.cache <<= @truncate(bits); // wont compile without @truncate. safe since we know bits<64 in this path. compiler dumb sometimes lol (it should know bits<64 too)
        }

        self.bitIndex +%= bits;
        self.bitIndex %= 64;
        return @truncate(output);
    }

    pub fn read(self: *BitReader, comptime T: type) T {
        switch (@typeInfo(T)) {
            .int => |info| {
                return @bitCast(self.readBits(info.bits));
            },
            .float => |info| {
                if (!(info.bits == 16 or info.bits == 32 or info.bits == 64)) @compileError("I CANT READ TYPE '" ++ @typeName(T) ++ "'");
                return @bitCast(self.readBits(info.bits));
            },
            .bool => {
                return self.readBits(1) == 1;
            },

            // holy im cooking
            .@"enum" => |info| {
                const backing_info = @typeInfo(info.tag_type).int;
                const xchg: @Int(backing_info.signedness, backing_info.bits) = @bitCast(self.readBits(backing_info.bits));
                return @enumFromInt(xchg);
            },

            .@"struct" => |info| {
                if (info.layout == .auto) {
                    @compileError("struct '" ++ @typeName(T) ++ "' must be `extern` or `packed` for a stable wire layout");
                }

                var value: T = undefined;
                self.readByteArray(std.mem.asBytes(&value));
                return value; // this is pass-by-value, no alloc needed
            },

            else => {
                @compileError("I CANT READ TYPE '" ++ @typeName(T) ++ "'");
            },
        }
    }

    /// Warning: you own this string now, free it later with the allocator you gave me! (unless you like leaking)
    ///
    /// string may be null, for interop with other languages
    pub fn readString(self: *BitReader, alloc: std.mem.Allocator) error{OutOfMemory}!?[]u8 {
        const len = self.read(u16);
        if (len == std.math.maxInt(u16)) {
            return null;
        }
        if (len == 0) {
            return &[0]u8{};
        }
        const slice: []u8 = try alloc.alloc(u8, len);
        self.readByteArray(slice);
        return slice;
    }

    fn readByteArray(self: *BitReader, slice: []u8) void {
        var ptr = slice; // make it mut

        // slice is a fat pointer, so it contains both ptr and len from the C# version
        while (ptr.len >= 8) : (ptr = ptr[8..]) {
            std.mem.writeInt(u64, ptr[0..8], self.readBits(64), .big);
        }
        while (ptr.len > 0) : (ptr = ptr[1..]) {
            ptr[0] = self.readBits(8);
        }
    }
};

test "symmetry" {
    const Thing = packed struct {
        f: f32,
        b: bool,
        u: u16,
    };
    const Thing2 = extern struct {
        f: f32 align(1),
        i: i64 align(256),
        u: u32 align(1),
    };

    var gpa = std.heap.DebugAllocator(.{}){};
    const DBG = gpa.allocator();
    defer _ = gpa.deinit();

    // write
    var w = try BitWriter.init(DBG, 1024);

    const a: u8 = 43;
    const b: bool = false;
    const c: i32 = -33337;
    const d: f64 = 71.4;
    const e: u63 = 4328948392435433;
    const f = "wazzup beijing";
    const g = Thing{ .f = -10, .b = false, .u = 5000 };
    const h = Thing2{ .f = -3249.51, .i = 88942121, .u = 400000000 };

    w.write(a);
    w.write(b);
    w.write(c);
    w.write(d);
    w.write(e);
    w.writeString(f);
    w.write(g);
    w.write(h);

    const arr = try w.conclude();
    defer DBG.free(arr);

    // read
    var r = BitReader.init(arr);
    try std.testing.expectEqual(r.read(u8), a);
    try std.testing.expectEqual(r.read(bool), b);
    try std.testing.expectEqual(r.read(i32), c);
    try std.testing.expectEqual(r.read(f64), d);
    try std.testing.expectEqual(r.read(u63), e);
    const fr = (try r.readString(DBG)).?;
    defer DBG.free(fr);
    try std.testing.expectEqualStrings(f, fr);
    try std.testing.expectEqual(r.read(Thing), g);
    try std.testing.expectEqual(r.read(Thing2), h);
}
