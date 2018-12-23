#region LICENSE

// Unless explicitly acquired and licensed from Licensor under another
// license, the contents of this file are subject to the Reciprocal Public
// License ("RPL") Version 1.5, or subsequent versions as allowed by the RPL,
// and You may not copy or use this file in either source code or executable
// form, except in compliance with the terms and conditions of the RPL.
// 
// All software distributed under the RPL is provided strictly on an "AS
// IS" basis, WITHOUT WARRANTY OF ANY KIND, EITHER EXPRESS OR IMPLIED, AND
// LICENSOR HEREBY DISCLAIMS ALL SUCH WARRANTIES, INCLUDING WITHOUT
// LIMITATION, ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE, QUIET ENJOYMENT, OR NON-INFRINGEMENT. See the RPL for specific
// language governing rights and limitations under the RPL.

#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace HQ.Common.FastMember
{
    /// <summary>
    ///     Provides a means of reading a sequence of objects as a data-reader, for example
    ///     for use with SqlBulkCopy or other data-base oriented code
    /// </summary>
    public class ObjectReader : DbDataReader
    {
        private readonly TypeAccessor accessor;
        private readonly BitArray allowNull;
        private readonly Type[] effectiveTypes;
        private readonly string[] memberNames;
        private bool active = true;

        private object current;
        private IEnumerator source;

        /// <summary>
        ///     Creates a new ObjectReader instance for reading the supplied data
        /// </summary>
        /// <param name="type">The expected Type of the information to be read</param>
        /// <param name="source">The sequence of objects to represent</param>
        /// <param name="members">The members that should be exposed to the reader</param>
        public ObjectReader(Type type, IEnumerable source, params string[] members)
        {
            if (source == null) throw new ArgumentOutOfRangeException("source");


            var allMembers = members == null || members.Length == 0;

            accessor = TypeAccessor.Create(type);
            if (accessor.GetMembersSupported)
            {
                var typeMembers = accessor.GetMembers();

                if (allMembers)
                {
                    members = new string[typeMembers.Count];
                    for (var i = 0; i < members.Length; i++) members[i] = typeMembers[i].Name;
                }

                this.allowNull = new BitArray(members.Length);
                effectiveTypes = new Type[members.Length];
                for (var i = 0; i < members.Length; i++)
                {
                    Type memberType = null;
                    var allowNull = true;
                    var hunt = members[i];
                    foreach (var member in typeMembers)
                        if (member.Name == hunt)
                        {
                            if (memberType == null)
                            {
                                var tmp = member.Type;
                                memberType = Nullable.GetUnderlyingType(tmp) ?? tmp;

                                allowNull = !(memberType.IsValueType && memberType == tmp);

                                // but keep checking, in case of duplicates
                            }
                            else
                            {
                                memberType = null; // duplicate found; say nothing
                                break;
                            }
                        }

                    this.allowNull[i] = allowNull;
                    effectiveTypes[i] = memberType ?? typeof(object);
                }
            }
            else if (allMembers)
            {
                throw new InvalidOperationException(
                    "Member information is not available for this type; the required members must be specified explicitly");
            }

            current = null;
            memberNames = (string[]) members.Clone();

            this.source = source.GetEnumerator();
        }


        public override int Depth => 0;

        public override bool HasRows => active;

        public override int RecordsAffected => 0;

        public override int FieldCount => memberNames.Length;

        public override bool IsClosed => source == null;

        public override object this[string name] => accessor[current, name] ?? DBNull.Value;

        /// <summary>
        ///     Gets the value of the current object in the member specified
        /// </summary>
        public override object this[int i] => accessor[current, memberNames[i]] ?? DBNull.Value;

        /// <summary>
        ///     Creates a new ObjectReader instance for reading the supplied data
        /// </summary>
        /// <param name="source">The sequence of objects to represent</param>
        /// <param name="members">The members that should be exposed to the reader</param>
        public static ObjectReader Create<T>(IEnumerable<T> source, params string[] members)
        {
            return new ObjectReader(typeof(T), source, members);
        }

        public override DataTable GetSchemaTable()
        {
            // these are the columns used by DataTable load
            var table = new DataTable
            {
                Columns =
                {
                    {"ColumnOrdinal", typeof(int)},
                    {"ColumnName", typeof(string)},
                    {"DataType", typeof(Type)},
                    {"ColumnSize", typeof(int)},
                    {"AllowDBNull", typeof(bool)}
                }
            };
            var rowData = new object[5];
            for (var i = 0; i < memberNames.Length; i++)
            {
                rowData[0] = i;
                rowData[1] = memberNames[i];
                rowData[2] = effectiveTypes == null ? typeof(object) : effectiveTypes[i];
                rowData[3] = -1;
                rowData[4] = allowNull == null ? true : allowNull[i];
                table.Rows.Add(rowData);
            }

            return table;
        }

        public override void Close()
        {
            Shutdown();
        }

        public override bool NextResult()
        {
            active = false;
            return false;
        }

        public override bool Read()
        {
            if (active)
            {
                var tmp = source;
                if (tmp != null && tmp.MoveNext())
                {
                    current = tmp.Current;
                    return true;
                }

                active = false;
            }

            current = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Shutdown();
        }

        private void Shutdown()
        {
            active = false;
            current = null;
            var tmp = source as IDisposable;
            source = null;
            if (tmp != null) tmp.Dispose();
        }

        public override bool GetBoolean(int i)
        {
            return (bool) this[i];
        }

        public override byte GetByte(int i)
        {
            return (byte) this[i];
        }

        public override long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length)
        {
            var s = (byte[]) this[i];
            var available = s.Length - (int) fieldOffset;
            if (available <= 0) return 0;

            var count = Math.Min(length, available);
            Buffer.BlockCopy(s, (int) fieldOffset, buffer, bufferoffset, count);
            return count;
        }

        public override char GetChar(int i)
        {
            return (char) this[i];
        }

        public override long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length)
        {
            var s = (string) this[i];
            var available = s.Length - (int) fieldoffset;
            if (available <= 0) return 0;

            var count = Math.Min(length, available);
            s.CopyTo((int) fieldoffset, buffer, bufferoffset, count);
            return count;
        }

        protected override DbDataReader GetDbDataReader(int i)
        {
            throw new NotSupportedException();
        }

        public override string GetDataTypeName(int i)
        {
            return (effectiveTypes == null ? typeof(object) : effectiveTypes[i]).Name;
        }

        public override DateTime GetDateTime(int i)
        {
            return (DateTime) this[i];
        }

        public override decimal GetDecimal(int i)
        {
            return (decimal) this[i];
        }

        public override double GetDouble(int i)
        {
            return (double) this[i];
        }

        public override Type GetFieldType(int i)
        {
            return effectiveTypes == null ? typeof(object) : effectiveTypes[i];
        }

        public override float GetFloat(int i)
        {
            return (float) this[i];
        }

        public override Guid GetGuid(int i)
        {
            return (Guid) this[i];
        }

        public override short GetInt16(int i)
        {
            return (short) this[i];
        }

        public override int GetInt32(int i)
        {
            return (int) this[i];
        }

        public override long GetInt64(int i)
        {
            return (long) this[i];
        }

        public override string GetName(int i)
        {
            return memberNames[i];
        }

        public override int GetOrdinal(string name)
        {
            return Array.IndexOf(memberNames, name);
        }

        public override string GetString(int i)
        {
            return (string) this[i];
        }

        public override object GetValue(int i)
        {
            return this[i];
        }

        public override IEnumerator GetEnumerator()
        {
            return new DbEnumerator(this);
        }

        public override int GetValues(object[] values)
        {
            // duplicate the key fields on the stack
            var members = memberNames;
            var current = this.current;
            var accessor = this.accessor;

            var count = Math.Min(values.Length, members.Length);
            for (var i = 0; i < count; i++) values[i] = accessor[current, members[i]] ?? DBNull.Value;
            return count;
        }

        public override bool IsDBNull(int i)
        {
            return this[i] is DBNull;
        }
    }
}
