
using System;
using System.Runtime.CompilerServices;
using static System.Runtime.CompilerServices.MethodImplOptions;
using System.Buffers.Binary;

// nanostream: unaligned bit writer and reader utility
namespace Nanostream
{
    /// <summary>
    /// Continuous bit writer. (i don't pad 0s, screw alignment)<br/>
    /// </summary>
    public class BitWriter
    {
        public BitWriter(int capacity = 4) { buf = new(capacity); current = 0; bitIndex = 0; }
        // public BitWriter(ByteList bytes) { buf = bytes; current = 0; bitIndex = 0; }
        // public void WriteBytesAligned(ReadOnlySpan<byte> bytes)
        // {
        //     if (bitIndex % 8 != 0) throw new InvalidOperationException("BitWriter must be aligned to write a span of bytes!");
        //     TRYFinishCurrentByteANDForcePush();
        //     buf.AddRange(bytes);
        // }
        ByteList buf;

        ulong current;
        ///<summary>number of bits written in <c>current</c>: 0–63</summary>
        byte bitIndex;

        /// <summary>
        /// Writes bits and moves forward.
        /// </summary>
        /// <param name="stuff">Expects LTR structure placed at the right like <c>0b00010100</c></param>
        /// <param name="bits">Number of bits to write from <c>stuff</c> (RTL).<br/>Maximum <c>64</c>.<br/>Ex: <c>stuff</c>=<c>0b0000000000000000000000000000000000000000000000000000000011110100</c> with bits = <c>5</c> adds <c>10100</c> to the stream.</param>
        // should test [MethodImpl(AggressiveInlining)], might drastically increase build size since so many Write() calls exist everywhere.
        void WriteBits(ulong stuff, byte bits = 8)
        {
            // clear the top bytes we arent writing
            if (bits < 64)
                stuff &= (1ul << bits) - 1ul;

            if (bitIndex + bits >= 64)
            {
                // write in 2 parts
                byte bits_1 = (byte)(64 - bitIndex);
                byte bits_2 = (byte)(bits - bits_1);

                ulong stuff_1 = stuff >> bits_2;
                current <<= bits_1;
                current |= stuff_1;
                buf.Add_u64(current);

                current = 0;
                if (bits_2 > 0)
                {
                    ulong stuff_2 = stuff & ((1ul << bits_2) - 1ul);
                    current <<= bits_2;
                    current |= stuff_2;
                }
            }
            else
            {
                // fits into current ulong
                current <<= bits;
                current |= stuff;
            }

            bitIndex += bits;
            bitIndex %= 64;
        }
        /// <summary>
        /// - bits: Finishes up the current byte. If this completes the u64 <c>current</c>, <c>WriteBits</c> automatically flushes the entire u64 it into the array.<br/>
        /// - bytes: In case we didn't finish the u64 <c>current</c>, flush it byte-by-byte anyway (not optimized, we don't write a whole u64).
        /// </summary>
        void FlushBitsAndBytes()
        {
            if (bitIndex > 0)
            {
                // finish up current byte exactly
                if (bitIndex % 8 != 0)
                    WriteBits(0, (byte)(8 - (bitIndex % 8)));

                // force push single bytes (if we added a u64 already: bitIndex == 0 -> skip)
                // bitIndex must be a multiple of 8 after WriteBits
                while (bitIndex > 0)
                {
                    buf.Add((byte)(current >> (bitIndex - 8)));
                    bitIndex -= 8;
                }
            }
        }
        /// <summary>
        /// Finalization function.
        /// Force write the current byte, which isn't normally written until it reaches 8 bits. Call after writing all bits.
        /// </summary>
        public byte[] Conclude()
        {
            FlushBitsAndBytes();
            return buf.ToArray();
        }
        [MethodImpl(AggressiveInlining)] public void Write(sbyte i) => Write((byte)i); // cast preserves the sign bit
        [MethodImpl(AggressiveInlining)] public void Write(short i) => Write((ushort)i); // cast preserves the sign bit
        [MethodImpl(AggressiveInlining)] public void Write(int i) => Write((uint)i);
        [MethodImpl(AggressiveInlining)] public void Write(long i) => Write((ulong)i);

        [MethodImpl(AggressiveInlining)]
        public void Write(byte i)
        {
            WriteBits(i, 8);
        }
        [MethodImpl(AggressiveInlining)]
        public void Write(ushort i)
        {
            WriteBits(i, 16);
        }
        [MethodImpl(AggressiveInlining)]
        public void Write(uint i)
        {
            WriteBits(i, 32);
        }
        [MethodImpl(AggressiveInlining)]
        public void Write(ulong i)
        {
            WriteBits(i, 64);
        }
        [MethodImpl(AggressiveInlining)] public void Write(bool i) => WriteBits(i ? (byte)1 : (byte)0, 1);
        [MethodImpl(AggressiveInlining)] public void Write(float i) => Write(System.BitConverter.SingleToInt32Bits(i));
        [MethodImpl(AggressiveInlining)] public void Write(double i) => Write(System.BitConverter.DoubleToInt64Bits(i));

        // [MethodImpl(AggressiveInlining)] public void Write(Vector2 i) { Write(i.x); Write(i.y); }

        // public void Write(Fix64 i) { Write(i.RawValue); }

        /// <summary>my Unsafe Code cannot be this Peak</summary>
        [MethodImpl(AggressiveInlining)]
        public unsafe void Write<TE>(TE value) where TE : unmanaged, Enum
        {
            int size = Unsafe.SizeOf<TE>();
            void* ptr = &value;
            ulong val = size switch
            {
                1 => *(byte*)ptr,
                2 => *(ushort*)ptr,
                4 => *(uint*)ptr,
                8 => *(ulong*)ptr,
            };
            WriteBits(val, (byte)(size * 8));
        }
        
        /// <summary>
        /// Write any unmanaged struct (size never changes; no List, only fixed arrays).<br/><br/>
        /// <b>Warning</b>: with should work on-the-wire and be compatible with Zig's read(TS), but it's untested.<br/>
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public unsafe void WriteStruct<TS>(TS value) where TS : unmanaged
        {
            byte* ptr = (byte*)&value;
            WriteByteArrayUnsafe(ptr, (ushort)sizeof(TS));
        }

        /// <summary>
        /// I'm not gonna recommend this for performance (if string is long).<br/>
        /// A big string is already a byte[], why use an unaligned bit writer for that? Just send that first.<br/><br/>
        /// <b>Warning</b>: If <c>s</c> is 65535 bytes or longer, I will write a null string instead.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public unsafe void WriteString(string s)
        {
            if (s == null)
            {
                Write(ushort.MaxValue);
                return;
            }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            if(bytes.Length >= ushort.MaxValue)
            {
                Write(ushort.MaxValue);
                return;
            }

            fixed (byte* ptr = bytes)
            {
                byte* p = ptr;
                Write((ushort)bytes.Length);
                WriteByteArrayUnsafe(p, bytes.Length);
            }
        }

        /// <summary>
        /// Initially not public API, don't use this unless you know what you are doing!<br/>
        /// Requires a managed reference to be "pinned" to use pointers. If you have a <c>byte[]</c>, use a <c>fixed(byte* ptr = bytes)</c> statement.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public unsafe void WriteByteArrayUnsafe(byte* ptr, int len)
        {
            int remaining = len;
            while (remaining >= 8)
            {
                WriteBits(BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(ptr, 8)), 64);
                ptr += 8;
                remaining -= 8;
            }
            while (remaining > 0)
            {
                WriteBits(*ptr, 8);
                ptr += 1;
                remaining -= 1;
            }
        }
    }

    /// <summary>
    /// Continuous bit reader. (warning: ensure your schemas match, dont read extra.)
    /// </summary>
    public class BitReader
    {
        // use this constructor to make a copy of a BitReader at a certain position in case you need to re-read stuff later (like after gaining info from later on the stream)
        public BitReader(BitReader snapshot)
        {
            this.buf = snapshot.buf; this.byteIndex = snapshot.byteIndex; this.bitIndex = snapshot.bitIndex;
            this.cache = snapshot.cache;
        }
        public BitReader(byte[] buf)
        {
            this.buf = buf; byteIndex = 0; bitIndex = 0;
            ObtainCache();
        }
        private byte[] buf;

        uint byteIndex;
        ulong cache;
        ///<summary>number of bits read in <c>cache</c>: 0–63</summary>
        byte bitIndex;

        /// <summary>
        /// Reads bits and moves forward.
        /// </summary>
        /// <param name="bits">Maximum <c>64</c>.</param>
        /// <returns>Bits are placed on the right side of output.<br/>Ex. ...<c>0000000</c>(You are here)<c>1100101</c>... with <c>bits</c>=<c>5</c> returns <c>0b0000000000000000000000000000000000000000000000000000000000011001</c>.</returns>
        ulong ReadBits(byte bits = 8)
        {
            if (bits == 0) return 0;
            ulong output = 0;

            if (bitIndex + bits >= 64)
            {
                // read in 2 parts
                byte bits_1 = (byte)(64 - bitIndex);
                byte bits_2 = (byte)(bits - bits_1);

                ulong reading_1 = cache >> (64 - bits_1);

                byteIndex += 8;
                ObtainCache();

                if (bits_2 > 0)
                {
                    ulong reading_2 = cache >> (64 - bits_2);
                    cache <<= bits_2;
                    output = (reading_1 << bits_2) | reading_2;
                }
                else
                    output = reading_1;
            }
            else
            {
                // the read fits into cache
                output = cache >> (64 - bits);
                cache <<= bits;
            }

            bitIndex += bits;
            bitIndex %= 64;
            return output;
        }
        unsafe void ObtainCache()
        {
            if (byteIndex + 8 <= buf.Length) // main branch (here 99.9% of the time)
                cache = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan((int)byteIndex, 8));

            // we are at the end of the byte[]; careful not to overextend the span
            else
            {
                cache = 0;
                uint idx = byteIndex;
                while (idx < buf.Length)
                {
                    cache <<= 8;
                    cache |= buf[idx];
                    idx++;
                }
                // move cache to the left
                cache <<= (byte)(64 - 8 * (buf.Length - byteIndex));
            }
        }
        [MethodImpl(AggressiveInlining)] public void Read(out sbyte value) => value = (sbyte)ReadBits(8);
        [MethodImpl(AggressiveInlining)] public void Read(out byte value) => value = (byte)ReadBits(8);

        [MethodImpl(AggressiveInlining)] public void Read(out short value) => value = (short)ReadBits(16);
        [MethodImpl(AggressiveInlining)] public void Read(out ushort value) => value = (ushort)ReadBits(16);

        [MethodImpl(AggressiveInlining)] public void Read(out int value) => value = (int)ReadBits(32);
        [MethodImpl(AggressiveInlining)] public void Read(out uint value) => value = (uint)ReadBits(32);

        [MethodImpl(AggressiveInlining)] public void Read(out long value) => value = (long)ReadBits(64);
        [MethodImpl(AggressiveInlining)] public void Read(out ulong value) => value = (ulong)ReadBits(64);

        [MethodImpl(AggressiveInlining)] public void Read(out bool value) => value = ReadBits(1) == 1;

        [MethodImpl(AggressiveInlining)] public void Read(out float value) => value = BitConverter.Int32BitsToSingle((int)ReadBits(32));
        [MethodImpl(AggressiveInlining)] public void Read(out double value) => value = BitConverter.Int64BitsToDouble((int)ReadBits(64));

        // [MethodImpl(AggressiveInlining)]
        // public void Read(out Vector2 value)
        // {
        //     Read(out float x);
        //     Read(out float y);
        //     value = new Vector2(x, y);
        // }
        // public void Read(out Fix64 value) => value = Fix64.FromRaw((long)ReadBits(64));


        [MethodImpl(AggressiveInlining)]
        public unsafe void Read<TE>(out TE value) where TE : unmanaged, Enum
        {
            switch (sizeof(TE))
            {
                case 1:
                {
                    Read(out byte v);
                    value = *(TE*)&v;
                    return;
                }
                case 2:
                {
                    Read(out ushort v);
                    value = *(TE*)&v;
                    return;
                }
                case 4:
                {
                    Read(out uint v);
                    value = *(TE*)&v;
                    return;
                }
                case 8:
                {
                    Read(out ulong v);
                    value = *(TE*)&v;
                    return;
                }
                default:
                {
                    throw new System.Exception();
                }
            }
        }

        ///<summary>Read any unmanaged struct (size never changes; no List, only fixed arrays).</summary>
        [MethodImpl(AggressiveInlining)]
        public unsafe void ReadStruct<TS>(out TS value) where TS : unmanaged
        {
            Unsafe.SkipInit(out value);
            byte* ptr = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
            ReadByteArrayUnsafe(ptr, sizeof(TS));
        }
        [MethodImpl(AggressiveInlining)]
        public unsafe void ReadString(out string s)
        {
            Read(out ushort len);
            if (len == ushort.MaxValue)
            {
                s = null;
                return;
            }
            if (len == 0)
            {
                s = string.Empty;
                return;
            }

            byte[] bytes = new byte[len];

            fixed (byte* ptr = bytes)
            {
                byte* p = ptr;
                ReadByteArrayUnsafe(p, bytes.Length);
            }

            s = System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Initially not public API, don't use this unless you know what you are doing!<br/>
        /// Requires a managed reference to be "pinned" to use pointers. If you have a <c>byte[]</c>, use a <c>fixed(byte* ptr = bytes)</c> statement.
        /// </summary>
        [MethodImpl(AggressiveInlining)]
        public unsafe void ReadByteArrayUnsafe(byte* ptr, int len)
        {
            int remaining = len;

            while (remaining >= 8)
            {
                BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(ptr, 8), ReadBits(64));
                ptr += 8;
                remaining -= 8;
            }
            while (remaining > 0)
            {
                *ptr = (byte)ReadBits(8);
                ptr += 1;
                remaining -= 1;
            }
        }
    }

    public class ByteList
    {
        public byte[] backingArr;
        public int Count;

        [MethodImpl(AggressiveInlining)] public ByteList(int capacity = 0) { backingArr = new byte[capacity]; Count = 0; }

        [MethodImpl(AggressiveInlining)]
        public void EnsureCapacity(int needed)
        {
            if (needed > backingArr.Length) Array.Resize(ref backingArr, Mathb.Max(backingArr.Length == 0 ? 4 : backingArr.Length * 2, needed));
        }

        [MethodImpl(AggressiveInlining)]
        public void Add(byte value)
        {
            EnsureCapacity(Count + 1);
            backingArr[Count++] = value;
        }
        [MethodImpl(AggressiveInlining)]
        public unsafe void Add_u64(ulong value)
        {
            EnsureCapacity(Count + 8);
            fixed (byte* p = &backingArr[Count])
                *(ulong*)p = BinaryPrimitives.ReverseEndianness(value);
            Count += 8;
        }
        [MethodImpl(AggressiveInlining)]
        public void AddRange(ReadOnlySpan<byte> values)
        {
            EnsureCapacity(Count + values.Length);
            values.CopyTo(backingArr.AsSpan(Count));
            Count += values.Length;
        }

        public byte this[int index]
        {
            [MethodImpl(AggressiveInlining)]
            get => backingArr[index];
            [MethodImpl(AggressiveInlining)]
            set => backingArr[index] = value;
        }
        [MethodImpl(AggressiveInlining)]
        public byte[] ToArray()
        {
            byte[] result = new byte[Count];
            Array.Copy(backingArr, result, Count);
            return result;
        }

        [MethodImpl(AggressiveInlining)] public Span<byte> AsSpan() => backingArr.AsSpan(0, Count);
    }
}