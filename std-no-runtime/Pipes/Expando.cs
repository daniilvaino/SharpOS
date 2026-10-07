using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// A set of named values with no class of its own (pipe spec Р31, Р32): a
    /// filter builds one from the fields of another object, a parser emits
    /// them. Access is by name through the indexer, or as `dynamic`.
    /// </summary>
    /// <remarks>
    /// It is in std, so every image has the same type under the same key: it
    /// travels through a native pipe like any [Message] type, and every reader
    /// gets a real Expando. Names keep the order they were added in and are
    /// compared ordinally.
    ///
    /// A value is null or of a type in the pipe catalog — a string, a number,
    /// an array of those, a std structure (DateTime, TimeSpan, Guid,
    /// ConsoleKeyInfo, Vector3), another Expando, any [Message] type. Anything
    /// else is refused when it is set, where the mistake is, not later when
    /// the Expando is sent.
    ///
    /// Read from a region it answers in place. Writing into it there follows
    /// the region's rule: only a value already in the region, or null. A new
    /// value — a number too, since it is boxed on the heap — is refused by the
    /// write barrier (RegionReferenceException): take ToHeap, change, Copy.
    ///
    /// Not System.Dynamic.ExpandoObject: no DLR here (`dynamic` binds through
    /// SharpOS.Std.Dynamic), and the name stays out of System because the
    /// behaviour is not that type's.
    /// </remarks>
    [Message]
    public sealed partial class Expando : IDictionary<string, object>
    {
        private string[] _names;
        private object[] _values;
        private int _count;

        public Expando()
        {
        }

        // Room for this many entries (a copy out of a region: ViewCopy).
        internal Expando(int capacity)
        {
            if (capacity > 0)
            {
                _names = new string[capacity];
                _values = new object[capacity];
            }
        }

        // An entry whose name is new and whose value is known to travel: a
        // copy out of a region (ViewCopy) — names of a type's fields are
        // distinct, and every value came out of a pipe.
        internal void AppendFresh(string name, object value)
        {
            if (_names == null || _count == _names.Length) Grow();
            _names[_count] = name;
            _values[_count] = value;
            _count++;
        }

        /// <summary>Fields in the set.</summary>
        public int Count => _count;

        /// <summary>The value under <paramref name="name"/>; KeyNotFoundException when there is none.</summary>
        public object this[string name]
        {
            get
            {
                int i = IndexOf(name);
                if (i < 0) throw new KeyNotFoundException("no field '" + name + "' in the Expando");
                return _values[i];
            }
            set => Set(name, value);
        }

        /// <summary>The names, in the order they were added. A copy.</summary>
        public ICollection<string> Keys
        {
            get
            {
                var names = new List<string>(_count);
                for (int i = 0; i < _count; i++) names.Add(_names[i]);
                return names;
            }
        }

        /// <summary>The values, in the order of their names. A copy.</summary>
        public ICollection<object> Values
        {
            get
            {
                var values = new List<object>(_count);
                for (int i = 0; i < _count; i++) values.Add(_values[i]);
                return values;
            }
        }

        public bool IsReadOnly => false;

        public bool ContainsKey(string name) => IndexOf(name) >= 0;

        public bool TryGetValue(string name, out object value)
        {
            int i = IndexOf(name);
            value = i >= 0 ? _values[i] : null;
            return i >= 0;
        }

        /// <summary>Adds a field; ArgumentException when the name is taken (the indexer replaces instead).</summary>
        public void Add(string name, object value)
        {
            if (IndexOf(name) >= 0) throw new ArgumentException("a field '" + name + "' is already in the Expando", nameof(name));
            Set(name, value);
        }

        public bool Remove(string name)
        {
            int i = IndexOf(name);
            if (i < 0) return false;
            for (int j = i; j < _count - 1; j++)
            {
                _names[j] = _names[j + 1];
                _values[j] = _values[j + 1];
            }
            _count--;
            _names[_count] = null;
            _values[_count] = null;
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                _names[i] = null;
                _values[i] = null;
            }
            _count = 0;
        }

        void ICollection<KeyValuePair<string, object>>.Add(KeyValuePair<string, object> item) => Add(item.Key, item.Value);

        bool ICollection<KeyValuePair<string, object>>.Contains(KeyValuePair<string, object> item)
            => TryGetValue(item.Key, out object value) && Equals(value, item.Value);

        bool ICollection<KeyValuePair<string, object>>.Remove(KeyValuePair<string, object> item)
            => TryGetValue(item.Key, out object value) && Equals(value, item.Value) && Remove(item.Key);

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0 || arrayIndex > array.Length - _count) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            for (int i = 0; i < _count; i++)
                array[arrayIndex + i] = new KeyValuePair<string, object>(_names[i], _values[i]);
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
                yield return new KeyValuePair<string, object>(_names[i], _values[i]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary><c>{ Name = value, … }</c>, nested Expandos the same way.</summary>
        public override string ToString()
        {
            var text = new System.Text.StringBuilder();
            text.Append("{ ");
            for (int i = 0; i < _count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(_names[i]).Append(" = ");
                object value = _values[i];
                if (value == null) text.Append("null");
                else if (value is string s) text.Append('"').Append(s).Append('"');
                else text.Append(value.ToString());
            }
            text.Append(" }");
            return text.ToString();
        }

        private int IndexOf(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            for (int i = 0; i < _count; i++)
                if (_names[i] == name) return i;
            return -1;
        }

        private void Set(string name, object value)
        {
            CheckTravels(value);
            int i = IndexOf(name);
            if (i >= 0)
            {
                _values[i] = value;
                return;
            }
            if (_names == null || _count == _names.Length) Grow();
            _names[_count] = name;
            _values[_count] = value;
            _count++;
        }

        private void Grow()
        {
            int capacity = _names == null || _names.Length == 0 ? 4 : _names.Length * 2;
            var names = new string[capacity];
            var values = new object[capacity];
            for (int j = 0; j < _count; j++)
            {
                names[j] = _names[j];
                values[j] = _values[j];
            }
            _names = names;
            _values = values;
        }

        private static unsafe void CheckTravels(object value)
        {
            if (value == null) return;
            ulong table = ObjectLayout.TableOf(Unsafe.As<object, ulong>(ref value));
            if (!MessageCatalog.Contains(table))
                throw new ArgumentException("the value cannot travel through a pipe: its type is not in the catalog", nameof(value));
        }
    }
}
