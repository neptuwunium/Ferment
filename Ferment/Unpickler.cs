/*
SPDX-FileCopyrightText: 2025-2026 Legiayayana

SPDX-License-Identifier: EUPL-1.2
*/

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ferment;

// we support all opcodes except FRAME (4), NEXT_BUFFER (5), and READONLY_BUFFER (5) though EXT1 (2), EXT2 (2) and EXT4 (2) might be implemented incorrectly.
public sealed class Unpickler : IDisposable {
	public Encoding Encoding;

	public Stream Stream;
	private byte[] StringBuffer;

	public Unpickler(Stream stream, Encoding? encoding = null) {
		Stream = stream;
		Encoding = encoding ?? Encoding.UTF8;
		StringBuffer = ArrayPool<byte>.Shared.Rent(0x100);
	}

	public Stack<object?> Stack { get; } = new();
	public Dictionary<int, object?> Memo { get; } = new();
	public int Protocol { get; set; } = 1;
	private object Mark { get; } = new();

	public void Dispose() {
		ArrayPool<byte>.Shared.Return(StringBuffer);
		Stream.Dispose();
	}

	public string ReadLine(Encoding? encoding = null) {
		var sb = StringBuffer;
		var p = 0;

		while (true) {
			var b = Stream.ReadByte();
			if (b == -1) {
				break;
			}

			if (b == 0x0A) {
				break;
			}

			sb[p++] = (byte) b;

			if (p == sb.Length) {
				var newSb = ArrayPool<byte>.Shared.Rent(sb.Length + 0x100);
				sb.CopyTo(newSb.AsSpan());
				ArrayPool<byte>.Shared.Return(sb);
				sb = StringBuffer = newSb;
			}
		}

		return (encoding ?? Encoding).GetString(sb.AsSpan(0, p));
	}

	public List<object?> PopMark() {
		var items = new List<object?>();

		while (Stack.Count > 0 && Stack.Peek() != Mark) {
			items.Add(Stack.Pop());
		}

		if (Stack.Count > 0) {
			Stack.Pop();
		}

		return items;
	}

	private void LoadProtocol() { // 0x80
		Protocol = Stream.ReadByte() - '0';
	}

	// ReSharper disable once MemberCanBeMadeStatic.Local
	private void LoadFrame() { // 0x95
		throw new NotSupportedException("FRAME opcode is not supported");
	}

	private void LoadPersId() { // P
		var pid = ReadLine();
		Stack.Push(pid);
	}

	private void LoadBinPersId() { // Q
		Stack.Push(Stack.Pop());
	}

	private void LoadNone() { // N
		Stack.Push(null);
	}

	private void LoadFalse() { // 0x89
		Stack.Push(false);
	}

	private void LoadTrue() { // 0x88
		Stack.Push(true);
	}

	private void LoadInt() { // I
		var value = ReadLine() ?? throw new InvalidDataException("Expected int value");
		switch (value) {
			case "00":
				Stack.Push(false);
				break;
			case "01":
				Stack.Push(true);
				break;
			default:
				Stack.Push(int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture));
				break;
		}
	}

	private void LoadBinInt() { // J
		Span<int> value = stackalloc int[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		Stack.Push(value[0]);
	}

	private void LoadBinInt1() { // K
		Stack.Push(Stream.ReadByte());
	}

	private void LoadBinInt2() { // M
		Span<short> value = stackalloc short[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		Stack.Push(value[0]);
	}

	private void LoadLong() { // L
		var value = ReadLine().TrimEnd('L') ?? throw new InvalidDataException("Expected long value");
		Stack.Push(BigInteger.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
	}

	private void LoadLong1() { // 0x8a
		var n = Stream.ReadByte();
		if (n > 8) {
			throw new InvalidDataException("Expected a reasonable long value");
		}

		Span<byte> value = stackalloc byte[8];
		Stream.ReadExactly(value[..n]);
		value[n..].Fill((byte) ((value[n - 1] & 0x80) != 0 ? 0xFF : 0x00));
		Stack.Push(BinaryPrimitives.ReadInt64LittleEndian(value));
	}

	private void LoadLong4() { // 0x8b
		Span<int> value = stackalloc int[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] is < 0 or > 8) {
			throw new InvalidDataException("Expected a reasonable long value");
		}

		var n = value[0];
		Span<byte> span = stackalloc byte[8];
		Stream.ReadExactly(span[..n]);
		value[n..].Fill((byte) ((value[n - 1] & 0x80) != 0 ? 0xFF : 0x00));
		Stack.Push(BinaryPrimitives.ReadInt64LittleEndian(span));
	}

	private void LoadFloat() { // F
		var value = ReadLine() ?? throw new InvalidDataException("Expected float value");
		Stack.Push(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
	}

	private void LoadBinFloat() { // G
		Span<double> value = stackalloc double[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReadDoubleBigEndian(MemoryMarshal.AsBytes(value));
		}

		Stack.Push(value[0]);
	}

	private void LoadString() { // S
		var value = ReadLine() ?? throw new InvalidDataException("Expected string value");
		if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '\'' || value[0] == '"')) {
			value = value[1..^1];
		}

		if (value.Contains("\\u000a", StringComparison.Ordinal)) {
			value = value.Replace("\\u000a", "\n", StringComparison.Ordinal);
		}

		Stack.Push(value);
	}

	private void LoadBinString() { // T
		Span<int> value = stackalloc int[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] is < 0 or > 0x1ffffff) {
			throw new InvalidDataException("Expected a reasonable buffer length");
		}

		var text = new byte[value[0]];
		Stream.ReadExactly(text.AsSpan());
		Stack.Push(text);
	}

	private void LoadBinString1() { // U
		var text = new byte[Stream.ReadByte()].AsSpan();
		Stream.ReadExactly(text);
		Stack.Push(Encoding.GetString(text));
	}

	private void LoadBinBytes() { // B
		Span<uint> value = stackalloc uint[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] > 0x1ffffff) {
			throw new InvalidDataException("Expected a reasonable buffer length");
		}

		var text = new byte[value[0]];
		Stream.ReadExactly(text);
		Stack.Push(text);
	}

	private void LoadBinBytes1() { // C
		var text = new byte[Stream.ReadByte()];
		Stream.ReadExactly(text);
		Stack.Push(text);
	}

	private void LoadBinBytes8() { // 0x8e
		Span<ulong> value = stackalloc ulong[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] > 0x1ffffff) {
			throw new InvalidDataException("Expected a reasonable buffer length");
		}

		var text = new byte[value[0]];
		Stream.ReadExactly(text);
		Stack.Push(text);
	}

	private void LoadUnicode() { // V
		var value = ReadLine(Encoding.UTF8) ?? throw new InvalidDataException("Expected string value");
		if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '\'' || value[0] == '"')) {
			value = value[1..^1];
		}

		Stack.Push(value.Replace("\\u000a", "\n", StringComparison.Ordinal));
	}

	private void LoadBinUnicode() { // 0x8d
		Span<long> value = stackalloc long[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] is < 0 or > 0x1ffffff) {
			throw new InvalidDataException("Expected a reasonable buffer length");
		}

		var text = new byte[value[0]];
		Stream.ReadExactly(text);
		Stack.Push(Encoding.UTF8.GetString(text));
	}

	private void LoadBinUnicode1() { // 0x8c
		var text = new byte[Stream.ReadByte()];
		Stream.ReadExactly(text);
		Stack.Push(Encoding.UTF8.GetString(text));
	}

	private void LoadByteArray8() { // 0x96
		Span<ulong> value = stackalloc ulong[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] > 0x1ffffff) {
			throw new InvalidDataException("Expected a reasonable buffer length");
		}

		var text = new byte[value[0]];
		Stream.ReadExactly(text);
		Stack.Push(text);
	}

	// ReSharper disable once MemberCanBeMadeStatic.Local
	private void LoadNextBuffer() { // 0x97
		throw new NotSupportedException("NEXT_BUFFER opcode is not supported");
	}

	// ReSharper disable once MemberCanBeMadeStatic.Local
	private void LoadReadOnlyBuffer() { // 0x98
		throw new NotSupportedException("READONLY_BUFFER opcode is not supported");
	}

	private void LoadTuple() { // t
		Stack.Push(PopMark());
	}

	private void LoadEmptyTuple() { // )
		Stack.Push(Array.Empty<object>());
	}

	private void LoadTuple1() { // 0x85
		Stack.Push(new[] { Stack.Pop() });
	}

	private void LoadTuple2() { // 0x86
		var b = Stack.Pop();
		var a = Stack.Pop();
		Stack.Push(new[] { a, b });
	}

	private void LoadTuple3() { // 0x87
		var c = Stack.Pop();
		var b = Stack.Pop();
		var a = Stack.Pop();
		Stack.Push(new[] { a, b, c });
	}

	private void LoadEmptyList() { // ]
		Stack.Push(Array.Empty<object>());
	}

	private void LoadEmptyDict() { // }
		Stack.Push(new Dictionary<object, object?>());
	}

	private void LoadEmptySet() { // 0x8f
		Stack.Push(new HashSet<object>());
	}

	private void LoadFrozenSet() { // 0x91
		Stack.Push(PopMark().ToHashSet());
	}

	private void LoadList() { // l
		Stack.Push(PopMark());
	}

	private void LoadDict() { // d
		var items = PopMark();
		var dict = new Dictionary<object, object?>();
		for (var i = 0; i < items.Count; i += 2) {
			if (items[i + 1] is not { } key) {
				throw new InvalidOperationException();
			}

			dict[key] = items[i];
		}

		Stack.Push(dict);
	}

	private void LoadInst() { // i
		var module = ReadLine();
		var name = ReadLine();
		var obj = new Dictionary<object, object?> {
			{ "__module__", module },
			{ "__name__", name },
			{ "__args__", PopMark() },
		};
		Stack.Push(obj);
	}

	private void LoadObj() { // o
		var args = PopMark();
		var cls = Stack.Pop();
		var obj = new Dictionary<object, object?> {
			{ "__name__", cls },
			{ "__args__", args },
		};
		Stack.Push(obj);
	}

	private void LoadNewObj() { // 0x81
		var args = Stack.Pop();
		var cls = Stack.Pop();
		var obj = new Dictionary<object, object?> {
			{ "__name__", cls },
			{ "__args__", args },
		};
		Stack.Push(obj);
	}

	private void LoadNewObjEx() { // 0x92
		var kwargs = Stack.Pop();
		var args = Stack.Pop();
		var cls = Stack.Pop();
		var obj = new Dictionary<object, object?> {
			{ "__name__", cls },
			{ "__args__", args },
			{ "__kwargs__", kwargs },
		};
		Stack.Push(obj);
	}

	private void LoadGlobal() { // c
		var module = ReadLine();
		var name = ReadLine();
		Stack.Push(new Dictionary<object, object?> {
			{ "__module__", module },
			{ "__name__", name },
		});
	}

	private void LoadStackGlobal() { // 0x93
		var args = Stack.Pop();
		var cls = Stack.Pop();
		var obj = new Dictionary<object, object?> {
			{ "__name__", cls },
			{ "__args__", args },
		};
		Stack.Push(obj);
	}

	private void LoadExt1() { // 0x82
		var ext = new byte[Stream.ReadByte()];
		Stream.ReadExactly(ext);
		Stack.Push(ext);
	}

	private void LoadExt2() { // 0x83
		Span<ushort> value = stackalloc ushort[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		var ext = new byte[value[0]];
		Stream.ReadExactly(ext);
		Stack.Push(ext);
	}

	private void LoadExt4() { // 0x84
		Span<uint> value = stackalloc uint[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		if (value[0] > 0x1ffffff) {
			throw new InvalidDataException("Expected a reasonable buffer length");
		}

		var ext = new byte[value[0]];
		Stream.ReadExactly(ext);
		Stack.Push(ext);
	}

	// ReSharper disable once MemberCanBeMadeStatic.Local
	private void LoadReduce() { // R
		var args = Stack.Pop();
		var cls = Stack.Pop();
		if (cls is not Dictionary<object, object> obj) {
			throw new InvalidDataException("Got garbage data");
		}

		if (args is not List<object> argArray) {
			throw new InvalidDataException("Got garbage data");
		}

		var module = (string) obj["__module__"];
		var name = (string) obj["__name__"];
		switch (module + "." + name) {
			case "copyreg._reconstructor":
			case "copy_reg._reconstructor":
				Stack.Push(new Dictionary<object, object>());
				break;
			case "__builtin__.set":
			case "__builtin__.frozenset":
			case "__builtin__.list":
			case "__builtin__.bytearray":
			case "__builtin__.int":
				Stack.Push(argArray[0]);
				break;
			default:
				throw new NotSupportedException($"REDUCE opcode is not supported for {module}.{name}");
		}
	}

	private void LoadPop() { // 0
		if (Stack.Count == 0) {
			PopMark();
		} else {
			Stack.Pop();
		}
	}

	private void LoadPopMark() { // 1
		PopMark();
	}

	private void LoadDup() { // 2
		Stack.Push(Stack.Peek());
	}

	private void LoadGet() { // g
		var index = int.Parse(ReadLine() ?? throw new InvalidDataException("Unexpected end of stream"), NumberStyles.Integer, CultureInfo.InvariantCulture);
		Stack.Push(Memo[index]);
	}

	private void LoadBinGet() { // h
		Stack.Push(Memo[Stream.ReadByte()]);
	}

	private void LoadBinGet4() { // j
		Span<int> value = stackalloc int[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		Stack.Push(Memo[value[0]]);
	}

	private void LoadPut() { // p
		var index = int.Parse(ReadLine() ?? throw new InvalidDataException("Unexpected end of stream"), NumberStyles.Integer, CultureInfo.InvariantCulture);
		Memo[index] = Stack.Peek();
	}

	private void LoadBinPut() { // q
		Memo[Stream.ReadByte()] = Stack.Peek();
	}

	private void LoadBinPut4() { // r
		Span<int> value = stackalloc int[1];
		Stream.ReadExactly(MemoryMarshal.AsBytes(value));
		if (!BitConverter.IsLittleEndian) {
			value[0] = BinaryPrimitives.ReverseEndianness(value[0]);
		}

		Memo[value[0]] = Stack.Peek();
	}

	private void LoadMemoize() { // 0x94
		var index = Memo.Count;
		Memo[index] = Stack.Peek();
	}

	private void LoadAppend() { // a
		var value = Stack.Pop();
		var obj = Stack.Peek();
		switch (obj) {
			case List<object?> list:
				list.Add(value);
				break;
			case object?[] array: {
				var newArray = new object?[array.Length + 1];
				array.CopyTo(newArray, 0);
				newArray[array.Length] = value;
				Stack.Pop();
				Stack.Push(newArray);
				break;
			}
			case HashSet<object?> set:
				set.Add(obj);
				break;
			default: throw new InvalidDataException("Unexpected type");
		}
	}

	private void LoadAppends() { // e
		var value = PopMark();
		var obj = Stack.Peek();
		switch (obj) {
			case List<object?> list:
				list.AddRange(value);
				break;
			case HashSet<object?> set:
				foreach (var item in value) {
					set.Add(item);
				}

				break;
			default: throw new InvalidDataException("Unexpected type");
		}
	}

	private void LoadSetItem() { // s
		var value = Stack.Pop();
		var key = Stack.Pop() ?? throw new InvalidDataException("Unexpected null key");
		var obj = Stack.Peek();

		switch (obj) {
			case Dictionary<object, object?> dict:
				dict[key] = value;
				break;
			default: throw new InvalidDataException("Unexpected type");
		}
	}

	private void LoadSetItems() { // u
		var items = PopMark();
		var obj = Stack.Peek();

		switch (obj) {
			case Dictionary<object, object?> dict:
				for (var i = 0; i < items.Count; i += 2) {
					var key = items[i] ?? throw new InvalidDataException("Unexpected null key");
					var value = items[i + 1];
					dict[key] = value;
				}

				break;
			default: throw new InvalidDataException("Unexpected type");
		}
	}

	private void LoadAddItems() { // 0x90
		var items = PopMark();
		var obj = Stack.Peek();

		switch (obj) {
			case HashSet<object?> set:
				foreach (var item in items) {
					set.Add(item);
				}

				break;
			case List<object?> list:
				list.AddRange(items);
				break;
			default: throw new InvalidDataException("Unexpected type");
		}
	}

	private void LoadBuild() { // b
		var obj = Stack.Pop();
		var inst = Stack.Peek();

		switch (inst) {
			case Dictionary<object, object?> dict: {
				if (obj is Dictionary<object, object?> dictObj) {
					foreach (var (key, val) in dictObj) {
						dict[key] = val;
					}
				} else {
					dict["__state__"] = obj;
				}

				break;
			}
			case List<object?> list: {
				if (obj is List<object?> listObj) {
					list.AddRange(listObj);
				} else {
					list.Add(obj);
				}

				break;
			}
			case HashSet<object?> set: {
				if (obj is HashSet<object?> setObj) {
					foreach (var entry in setObj) {
						set.Add(entry);
					}
				} else {
					set.Add(obj);
				}

				break;
			}
			default: throw new InvalidDataException("Unexpected type");
		}
	}

	private void LoadMark() { // (
		Stack.Push(Mark);
	}

	public object? LoadStop() => // .
		Stack.Pop();

	public object? Read() {
		while (!Stream.CanSeek || Stream.Position < Stream.Length) {
			var opcode = (char) Stream.ReadByte();

			switch (opcode) {
				case '.': {
					return LoadStop();
				}

				case (char) 0x80:
					LoadProtocol();
					continue;

				case (char) 0x95:
					LoadFrame();
					continue;

				case 'P':
					LoadPersId();
					continue;

				case 'Q':
					LoadBinPersId();
					continue;

				case 'N':
					LoadNone();
					continue;

				case (char) 0x89:
					LoadFalse();
					continue;

				case (char) 0x88:
					LoadTrue();
					continue;

				case 'I':
					LoadInt();
					continue;

				case 'J':
					LoadBinInt();
					continue;

				case 'K':
					LoadBinInt1();
					continue;

				case 'M':
					LoadBinInt2();
					continue;

				case 'L':
					LoadLong();
					continue;

				case (char) 0x8a:
					LoadLong1();
					continue;

				case (char) 0x8b:
					LoadLong4();
					continue;

				case 'F':
					LoadFloat();
					continue;

				case 'G':
					LoadBinFloat();
					continue;

				case 'S':
					LoadString();
					continue;

				case 'T':
					LoadBinString();
					continue;

				case 'U':
					LoadBinString1();
					continue;

				case 'B':
					LoadBinBytes();
					continue;

				case 'C':
					LoadBinBytes1();
					continue;

				case (char) 0x8e:
					LoadBinBytes8();
					continue;

				case 'V':
					LoadUnicode();
					continue;

				case (char) 0x8d:
					LoadBinUnicode();
					continue;

				case (char) 0x8c:
					LoadBinUnicode1();
					continue;

				case (char) 0x96:
					LoadByteArray8();
					continue;

				case (char) 0x97:
					LoadNextBuffer();
					continue;

				case (char) 0x98:
					LoadReadOnlyBuffer();
					continue;

				case 't':
					LoadTuple();
					continue;

				case ')':
					LoadEmptyTuple();
					continue;

				case (char) 0x85:
					LoadTuple1();
					continue;

				case (char) 0x86:
					LoadTuple2();
					continue;

				case (char) 0x87:
					LoadTuple3();
					continue;

				case ']':
					LoadEmptyList();
					continue;

				case '}':
					LoadEmptyDict();
					continue;

				case (char) 0x8f:
					LoadEmptySet();
					continue;

				case (char) 0x91:
					LoadFrozenSet();
					continue;

				case 'l':
					LoadList();
					continue;

				case 'd':
					LoadDict();
					continue;

				case 'i':
					LoadInst();
					continue;

				case 'o':
					LoadObj();
					continue;

				case (char) 0x81:
					LoadNewObj();
					continue;

				case (char) 0x92:
					LoadNewObjEx();
					continue;

				case 'c':
					LoadGlobal();
					continue;

				case (char) 0x93:
					LoadStackGlobal();
					continue;

				case (char) 0x82:
					LoadExt1();
					continue;

				case (char) 0x83:
					LoadExt2();
					continue;

				case (char) 0x84:
					LoadExt4();
					continue;

				case 'R':
					LoadReduce();
					continue;

				case '0':
					LoadPop();
					continue;

				case '1':
					LoadPopMark();
					continue;

				case '2':
					LoadDup();
					continue;

				case 'g':
					LoadGet();
					continue;

				case 'h':
					LoadBinGet();
					continue;

				case 'j':
					LoadBinGet4();
					continue;

				case 'p':
					LoadPut();
					continue;

				case 'q':
					LoadBinPut();
					continue;

				case 'r':
					LoadBinPut4();
					continue;

				case (char) 0x94:
					LoadMemoize();
					continue;

				case 'a':
					LoadAppend();
					continue;

				case 'e':
					LoadAppends();
					continue;

				case 's':
					LoadSetItem();
					continue;

				case 'u':
					LoadSetItems();
					continue;

				case (char) 0x90:
					LoadAddItems();
					continue;

				case 'b':
					LoadBuild();
					continue;

				case '(':
					LoadMark();
					continue;

				default:
					throw new InvalidDataException($"Unexpected opcode: {opcode}");
			}
		}

		throw new UnreachableException("Pickle is truncated");
	}
}
