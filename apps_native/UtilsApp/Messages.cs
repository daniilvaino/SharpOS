using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace UtilsApp
{
    /// <summary>
    /// What a filter works on: a message of the input, as it came, or a line
    /// of bytes (a file, a byte[] stream), for which there is no message.
    /// </summary>
    internal sealed unsafe class Item
    {
        public RawRegion Region;
        public string Line;

        // A line of bytes: where from, how many bytes it took, and whether it
        // ended with a line feed.
        public string File;
        public int RawBytes;
        public bool Unterminated;

        /// <summary>What the message reads as: a string itself, anything else as the screen's line for it.</summary>
        public string Text()
        {
            if (Line != null) return Line;
            View root = Region.Root;
            if (root.TryGetChars(out char* chars, out int length)) return new string(new ReadOnlySpan<char>(chars, length));
            return root.IsNull ? "null" : root.ToString();
        }

        public void Dispose() => Region?.Dispose();
    }

    /// <summary>The files named, or else the standard input; bytes become lines, other messages stay messages.</summary>
    internal sealed unsafe class Source
    {
        private readonly List<string> _files;
        private int _nextFile;
        private AppFile _file;
        private string _fileName;
        private readonly byte[] _piece = new byte[64 * 1024];

        public readonly RawPipeReader Input;
        private bool _inputOver;

        // A line being gathered, across the pieces; the lines ready to go.
        private byte[] _carry = new byte[256];
        private int _carried;
        private readonly List<Item> _ready = new List<Item>();
        private int _readyAt;
        private bool _atStart = true;

        /// <summary>A file that could not be read was named, and said so.</summary>
        public bool Failed;

        public Source(List<string> files)
        {
            _files = files;
            Input = files.Count == 0 ? Pipe.Read() : null;
        }

        public Item Next()
        {
            while (true)
            {
                if (_readyAt < _ready.Count) return _ready[_readyAt++];
                _ready.Clear();
                _readyAt = 0;

                if (_file != null)
                {
                    int n = _file.Read(new Span<byte>(_piece));
                    if (n > 0)
                    {
                        fixed (byte* p = _piece) Feed(p, n);
                        continue;
                    }
                    FlushLine();
                    _file.Dispose();
                    _file = null;
                    continue;
                }
                if (_nextFile < _files.Count)
                {
                    _fileName = _files[_nextFile++];
                    _atStart = true;
                    try { _file = AppFile.Open(_fileName, AppFile.ModeRead); }
                    catch (IOException e)
                    {
                        Console.Error.WriteLine(_fileName + ": " + e.Message);
                        Failed = true;
                    }
                    continue;
                }
                if (Input == null || _inputOver) return null;

                RawRegion region = Input.Next(null);
                if (region == null)
                {
                    _inputOver = true;
                    FlushLine();
                    continue;
                }
                if (region.Root.TryGetBytes(out byte* bytes, out int count))
                {
                    Feed(bytes, count);
                    region.Dispose();
                    continue;
                }
                return new Item { Region = region };
            }
        }

        // Stops reading: what is before this stage finds its output closed.
        public void Close()
        {
            _file?.Dispose();
            _file = null;
            Input?.Dispose();
        }

        private void Feed(byte* bytes, int count)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                Gather(bytes + start, i - start);
                AddLine(false);
                start = i + 1;
            }
            Gather(bytes + start, count - start);
        }

        private void Gather(byte* bytes, int count)
        {
            if (_carried + count > _carry.Length)
            {
                var grown = new byte[Math.Max(_carry.Length * 2, _carried + count)];
                Array.Copy(_carry, grown, _carried);
                _carry = grown;
            }
            new ReadOnlySpan<byte>(bytes, count).CopyTo(new Span<byte>(_carry, _carried, count));
            _carried += count;
        }

        private void FlushLine()
        {
            if (_carried > 0) AddLine(true);
        }

        private void AddLine(bool unterminated)
        {
            int from = 0, length = _carried;
            if (_atStart && length >= 3 && _carry[0] == 0xEF && _carry[1] == 0xBB && _carry[2] == 0xBF)
            {
                from = 3;
                length -= 3;
            }
            _atStart = false;
            if (length > 0 && _carry[from + length - 1] == (byte)'\r') length--;
            _ready.Add(new Item
            {
                Line = Encoding.UTF8.GetString(_carry, from, length),
                File = _fileName,
                RawBytes = _carried + (unterminated ? 0 : 1),
                Unterminated = unterminated,
            });
            _carried = 0;
        }
    }

    /// <summary>
    /// The standard output of a filter: messages go on as they came, the block
    /// itself under the input's description; lines as string messages.
    /// </summary>
    internal sealed unsafe class Output : IDisposable
    {
        private readonly RawPipeReader _input;
        private int _handle;
        private bool _opened, _screen;
        private PipeWriter<string> _text;

        public Output(Source source) => _input = source.Input;

        public Output(RawPipeReader input) => _input = input;

        public void Send(Item item)
        {
            if (item.Line != null) Line(item.Line);
            else Forward(item.Region);
        }

        public void Line(string line)
        {
            _text ??= Pipe.Write<string>();
            _text.Copy(line);
        }

        public void Forward(RawRegion region)
        {
            if (!_opened)
            {
                _opened = true;
                _handle = Pipe.TakeOutput("the input's type, passed on");
                _screen = _handle == 0;
                if (!_screen)
                {
                    PipeStatus opened = PipeTransport.OpenEnd(_handle, _input.Schema, _input.RootKey, out string error);
                    if (opened != PipeStatus.Ok)
                    {
                        region.Dispose();
                        throw new PipeException(opened, "the output: " + (error ?? Pipe.Explain(opened)));
                    }
                }
            }
            if (_screen)
            {
                try { ScreenText.Print(region.Root); }
                finally { region.Dispose(); }
                return;
            }
            PipeStatus sent = PipeTransport.Send(_handle, region.Block, region.Length);
            if (sent != PipeStatus.Ok)
            {
                region.Dispose();
                throw new PipeException(sent, "the output: " + Pipe.Explain(sent), _handle);
            }
            region.End("the region went on to the standard output");
        }

        public void Dispose()
        {
            if (_text != null)
            {
                _text.Dispose();
                return;
            }
            if (_opened)
            {
                if (_screen) ScreenText.End();
                else PipeTransport.End(_handle);
                return;
            }
            // Nothing passed: the output still carries the input's type, and its end.
            byte[] schema = null;
            ulong rootKey = 0;
            if (_input != null)
            {
                schema = _input.Schema;
                rootKey = _input.RootKey;
                if (schema == null || rootKey == 0) schema = PipeTransport.Schema(_input.Handle, out rootKey);
            }
            if (schema == null)
            {
                Pipe.Write<string>().Dispose();
                return;
            }
            int handle = Pipe.TakeOutput("the input's type, passed on");
            if (handle == 0) return;
            PipeTransport.OpenEnd(handle, schema, rootKey, out _);
            PipeTransport.End(handle);
        }
    }

    /// <summary>What sort and uniq compare: a field of an object (a.b), a column of a line, or all of its text.</summary>
    internal sealed class KeyRule
    {
        private readonly string[] _path;
        private readonly int _column;   // 1 up; 0: not a column
        private readonly bool _numeric, _ignoreCase;

        public KeyRule(string key, bool numeric, bool ignoreCase)
        {
            _numeric = numeric;
            _ignoreCase = ignoreCase;
            if (string.IsNullOrEmpty(key)) return;
            if (int.TryParse(key, out int column) && column > 0) _column = column;
            else _path = key.Split('.');
        }

        public Key Of(Item item)
        {
            var key = new Key();
            if (_path != null && item.Region != null)
            {
                View v = item.Region.Root;
                foreach (string name in _path)
                {
                    if (v.Kind != ViewKind.Object || !v.Has(name))
                    {
                        key.Text = "";
                        return key;
                    }
                    v = v[name];
                }
                // A number is a number, without being told.
                if (v.Kind == ViewKind.Integer || v.Kind == ViewKind.Float)
                {
                    key.IsNumber = true;
                    key.Number = (double)v;
                }
                key.Text = v.IsNull ? "" : v.ToString();
            }
            else if (_path != null)
            {
                key.Text = "";
            }
            else
            {
                key.Text = _column > 0 ? Column(item.Text(), _column) : item.Text();
            }
            if (_numeric && !key.IsNumber) key.IsNumber = LeadingNumber(key.Text, out key.Number);
            return key;
        }

        public int Compare(in Key a, in Key b)
        {
            if (a.IsNumber && b.IsNumber)
            {
                if (a.Number < b.Number) return -1;
                if (a.Number > b.Number) return 1;
            }
            else if (a.IsNumber != b.IsNumber)
            {
                return a.IsNumber ? -1 : 1;
            }
            return _ignoreCase ? string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase)
                               : string.CompareOrdinal(a.Text, b.Text);
        }

        private static string Column(string text, int column)
        {
            int at = 0, seen = 0;
            while (at < text.Length)
            {
                while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                int start = at;
                while (at < text.Length && !char.IsWhiteSpace(text[at])) at++;
                if (at > start && ++seen == column) return text.Substring(start, at - start);
            }
            return "";
        }

        private static bool LeadingNumber(string text, out double number)
        {
            int at = 0;
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            int start = at;
            if (at < text.Length && (text[at] == '-' || text[at] == '+')) at++;
            while (at < text.Length && ((text[at] >= '0' && text[at] <= '9') || text[at] == '.')) at++;
            return double.TryParse(text.Substring(start, at - start), out number);
        }
    }

    internal struct Key
    {
        public bool IsNumber;
        public double Number;
        public string Text;
    }
}
