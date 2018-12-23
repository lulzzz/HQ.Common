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
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace HQ.Common.FastMember
{
    /// <summary>
    ///     Provides by-name member-access to objects of a given type
    /// </summary>
    public abstract class TypeAccessor
    {
        // hash-table has better read-without-locking semantics than dictionary
        private static readonly Hashtable publicAccessorsOnly = new Hashtable(), nonPublicAccessors = new Hashtable();

        private static AssemblyBuilder assembly;
        private static ModuleBuilder module;
        private static int counter;

        private static readonly MethodInfo tryGetValue = typeof(Dictionary<string, int>).GetMethod("TryGetValue");

        private static readonly MethodInfo strinqEquals =
            typeof(string).GetMethod("op_Equality", new[] {typeof(string), typeof(string)});

        /// <summary>
        ///     Does this type support new instances via a parameterless constructor?
        /// </summary>
        public virtual bool CreateNewSupported => false;

        /// <summary>
        ///     Can this type be queried for member availability?
        /// </summary>
        public virtual bool GetMembersSupported => false;

        public virtual PropertyInfo[] CachedProperties { get; private set; }

        public virtual FieldInfo[] CachedFields { get; private set; }

        public virtual MemberInfo[] CachedMembers { get; private set; }

        /// <summary>
        ///     Get or set the value of a named member on the target instance
        /// </summary>
        public abstract object this[object target, string name] { get; set; }

        /// <summary>
        ///     Create a new instance of this type
        /// </summary>
        public virtual object CreateNew()
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///     Query the members available for this type
        /// </summary>
        public virtual MemberSet GetMembers()
        {
            throw new NotSupportedException();
        }

        /// <summary>
        ///     Provides a type-specific accessor, allowing by-name access for all objects of that type
        /// </summary>
        /// <remarks>The accessor is cached internally; a pre-existing accessor may be returned</remarks>
        public static TypeAccessor Create(Type type)
        {
            return Create(type, false);
        }

        /// <summary>
        ///     Provides a type-specific accessor, allowing by-name access for all objects of that type
        /// </summary>
        /// <remarks>The accessor is cached internally; a pre-existing accessor may be returned</remarks>
        public static TypeAccessor Create(Type type, bool allowNonPublicAccessors)
        {
            if (type == null) throw new ArgumentNullException("type");
            var lookup = allowNonPublicAccessors ? nonPublicAccessors : publicAccessorsOnly;
            var obj = (TypeAccessor) lookup[type];
            if (obj != null) return obj;

            lock (lookup)
            {
                // double-check
                obj = (TypeAccessor) lookup[type];
                if (obj != null) return obj;

                obj = CreateNew(type, allowNonPublicAccessors);

                lookup[type] = obj;
                return obj;
            }
        }

        private static int GetNextCounterValue()
        {
            return Interlocked.Increment(ref counter);
        }

        private static void WriteMapImpl(ILGenerator il, Type type, List<MemberInfo> members, FieldBuilder mapField,
            bool allowNonPublicAccessors, bool isGet)
        {
            OpCode obj, index, value;

            var fail = il.DefineLabel();
            if (mapField == null)
            {
                index = OpCodes.Ldarg_0;
                obj = OpCodes.Ldarg_1;
                value = OpCodes.Ldarg_2;
            }
            else
            {
                il.DeclareLocal(typeof(int));
                index = OpCodes.Ldloc_0;
                obj = OpCodes.Ldarg_1;
                value = OpCodes.Ldarg_3;

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, mapField);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldloca_S, (byte) 0);
                il.EmitCall(OpCodes.Callvirt, tryGetValue, null);
                il.Emit(OpCodes.Brfalse, fail);
            }

            var labels = new Label[members.Count];
            for (var i = 0; i < labels.Length; i++) labels[i] = il.DefineLabel();
            il.Emit(index);
            il.Emit(OpCodes.Switch, labels);
            il.MarkLabel(fail);
            il.Emit(OpCodes.Ldstr, "name");
            il.Emit(OpCodes.Newobj, typeof(ArgumentOutOfRangeException).GetConstructor(new[] {typeof(string)}));
            il.Emit(OpCodes.Throw);
            for (var i = 0; i < labels.Length; i++)
            {
                il.MarkLabel(labels[i]);
                var member = members[i];
                var isFail = true;
                FieldInfo field;
                PropertyInfo prop;
                if ((field = member as FieldInfo) != null)
                {
                    il.Emit(obj);
                    Cast(il, type, true);
                    if (isGet)
                    {
                        il.Emit(OpCodes.Ldfld, field);
                        if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
                    }
                    else
                    {
                        il.Emit(value);
                        Cast(il, field.FieldType, false);
                        il.Emit(OpCodes.Stfld, field);
                    }

                    il.Emit(OpCodes.Ret);
                    isFail = false;
                }
                else if ((prop = member as PropertyInfo) != null)
                {
                    MethodInfo accessor;
                    if (prop.CanRead &&
                        (accessor = isGet
                            ? prop.GetGetMethod(allowNonPublicAccessors)
                            : prop.GetSetMethod(allowNonPublicAccessors)) != null)
                    {
                        il.Emit(obj);
                        Cast(il, type, true);
                        if (isGet)
                        {
                            il.EmitCall(type.IsValueType ? OpCodes.Call : OpCodes.Callvirt, accessor, null);
                            if (prop.PropertyType.IsValueType) il.Emit(OpCodes.Box, prop.PropertyType);
                        }
                        else
                        {
                            il.Emit(value);
                            Cast(il, prop.PropertyType, false);
                            il.EmitCall(type.IsValueType ? OpCodes.Call : OpCodes.Callvirt, accessor, null);
                        }

                        il.Emit(OpCodes.Ret);
                        isFail = false;
                    }
                }

                if (isFail) il.Emit(OpCodes.Br, fail);
            }
        }

        private static bool IsFullyPublic(Type type, PropertyInfo[] props, bool allowNonPublicAccessors)
        {
            while (type.IsNestedPublic) type = type.DeclaringType;
            if (!type.IsPublic) return false;

            if (allowNonPublicAccessors)
                for (var i = 0; i < props.Length; i++)
                {
                    if (props[i].GetGetMethod(true) != null && props[i].GetGetMethod(false) == null)
                        return false; // non-public getter
                    if (props[i].GetSetMethod(true) != null && props[i].GetSetMethod(false) == null)
                        return false; // non-public setter
                }

            return true;
        }

        private static TypeAccessor CreateNew(Type type, bool allowNonPublicAccessors)
        {
            if (typeof(IDynamicMetaObjectProvider).IsAssignableFrom(type)) return DynamicAccessor.Singleton;

            var flags = allowNonPublicAccessors
                ? BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                : BindingFlags.Public | BindingFlags.Instance;

            var props = type.GetProperties(flags);
            var fields = type.GetFields(flags);

            var map = new Dictionary<string, int>();
            var members = new List<MemberInfo>(props.Length + fields.Length);
            var i = 0;
            foreach (var prop in props)
                if (!map.ContainsKey(prop.Name) && prop.GetIndexParameters().Length == 0)
                {
                    map.Add(prop.Name, i++);
                    members.Add(prop);
                }

            foreach (var field in fields)
                if (!map.ContainsKey(field.Name))
                {
                    map.Add(field.Name, i++);
                    members.Add(field);
                }

            ConstructorInfo ctor = null;
            if (type.IsClass && !type.IsAbstract) ctor = type.GetConstructor(Type.EmptyTypes);
            ILGenerator il;
            if (!IsFullyPublic(type, props, allowNonPublicAccessors))
            {
                DynamicMethod dynGetter = new DynamicMethod(type.FullName + "_get", typeof(object),
                        new[] {typeof(int), typeof(object)}, type, true),
                    dynSetter = new DynamicMethod(type.FullName + "_set", null,
                        new[] {typeof(int), typeof(object), typeof(object)}, type, true);
                WriteMapImpl(dynGetter.GetILGenerator(), type, members, null, allowNonPublicAccessors, true);
                WriteMapImpl(dynSetter.GetILGenerator(), type, members, null, allowNonPublicAccessors, false);
                DynamicMethod dynCtor = null;
                if (ctor != null)
                {
                    dynCtor = new DynamicMethod(type.FullName + "_ctor", typeof(object), Type.EmptyTypes, type, true);
                    il = dynCtor.GetILGenerator();
                    il.Emit(OpCodes.Newobj, ctor);
                    il.Emit(OpCodes.Ret);
                }

                return new DelegateAccessor(
                    map,
                    (Func<int, object, object>) dynGetter.CreateDelegate(typeof(Func<int, object, object>)),
                    (Action<int, object, object>) dynSetter.CreateDelegate(typeof(Action<int, object, object>)),
                    dynCtor == null ? null : (Func<object>) dynCtor.CreateDelegate(typeof(Func<object>)), type);
            }

            // note this region is synchronized; only one is being created at a time so we don't need to stress about the builders
            if (assembly == null)
            {
                var name = new AssemblyName("FastMember_dynamic");
                assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
                module = assembly.DefineDynamicModule(name.Name);
            }

            var attribs = typeof(TypeAccessor).Attributes;
            var tb = module.DefineType("FastMember_dynamic." + type.Name + "_" + GetNextCounterValue(),
                (attribs | TypeAttributes.Sealed | TypeAttributes.Public) &
                ~(TypeAttributes.Abstract | TypeAttributes.NotPublic), typeof(RuntimeTypeAccessor));

            il = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[]
            {
                typeof(Dictionary<string, int>)
            }).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            var mapField = tb.DefineField("_map", typeof(Dictionary<string, int>),
                FieldAttributes.InitOnly | FieldAttributes.Private);
            il.Emit(OpCodes.Stfld, mapField);
            il.Emit(OpCodes.Ret);


            var indexer = typeof(TypeAccessor).GetProperty("Item");
            MethodInfo baseGetter = indexer.GetGetMethod(), baseSetter = indexer.GetSetMethod();
            var body = tb.DefineMethod(baseGetter.Name, baseGetter.Attributes & ~MethodAttributes.Abstract,
                typeof(object), new[] {typeof(object), typeof(string)});
            il = body.GetILGenerator();
            WriteMapImpl(il, type, members, mapField, allowNonPublicAccessors, true);
            tb.DefineMethodOverride(body, baseGetter);

            body = tb.DefineMethod(baseSetter.Name, baseSetter.Attributes & ~MethodAttributes.Abstract, null,
                new[] {typeof(object), typeof(string), typeof(object)});
            il = body.GetILGenerator();
            WriteMapImpl(il, type, members, mapField, allowNonPublicAccessors, false);
            tb.DefineMethodOverride(body, baseSetter);

            MethodInfo baseMethod;
            if (ctor != null)
            {
                baseMethod = typeof(TypeAccessor).GetProperty("CreateNewSupported").GetGetMethod();
                body = tb.DefineMethod(baseMethod.Name, baseMethod.Attributes, baseMethod.ReturnType, Type.EmptyTypes);
                il = body.GetILGenerator();
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                tb.DefineMethodOverride(body, baseMethod);

                baseMethod = typeof(TypeAccessor).GetMethod("CreateNew");
                body = tb.DefineMethod(baseMethod.Name, baseMethod.Attributes, baseMethod.ReturnType, Type.EmptyTypes);
                il = body.GetILGenerator();
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Ret);
                tb.DefineMethodOverride(body, baseMethod);
            }

            baseMethod = typeof(RuntimeTypeAccessor).GetProperty("Type", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetGetMethod(true);
            body = tb.DefineMethod(baseMethod.Name, baseMethod.Attributes & ~MethodAttributes.Abstract,
                baseMethod.ReturnType, Type.EmptyTypes);
            il = body.GetILGenerator();
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(body, baseMethod);

            var accessor = (TypeAccessor) Activator.CreateInstance(tb.CreateTypeInfo().AsType(), map);

            accessor.CachedProperties = props;
            accessor.CachedFields = fields;
            accessor.CachedMembers =
                accessor.CachedProperties.Cast<MemberInfo>().Concat(accessor.CachedFields).ToArray();

            return accessor;
        }

        private static void Cast(ILGenerator il, Type type, bool valueAsPointer)
        {
            if (type == typeof(object))
            {
            }
            else if (type.IsValueType)
            {
                if (valueAsPointer)
                    il.Emit(OpCodes.Unbox, type);
                else
                    il.Emit(OpCodes.Unbox_Any, type);
            }
            else
            {
                il.Emit(OpCodes.Castclass, type);
            }
        }

        private sealed class DynamicAccessor : TypeAccessor
        {
            public static readonly DynamicAccessor Singleton = new DynamicAccessor();

            private DynamicAccessor()
            {
            }

            public override object this[object target, string name]
            {
                get => CallSiteCache.GetValue(name, target);
                set => CallSiteCache.SetValue(name, target, value);
            }
        }

        /// <summary>
        ///     A TypeAccessor based on a Type implementation, with available member metadata
        /// </summary>
        protected abstract class RuntimeTypeAccessor : TypeAccessor
        {
            private MemberSet members;

            /// <summary>
            ///     Returns the Type represented by this accessor
            /// </summary>
            protected abstract Type Type { get; }

            /// <summary>
            ///     Can this type be queried for member availability?
            /// </summary>
            public override bool GetMembersSupported => true;

            /// <summary>
            ///     Query the members available for this type
            /// </summary>
            public override MemberSet GetMembers()
            {
                return members ?? (members = new MemberSet(Type));
            }
        }

        private sealed class DelegateAccessor : RuntimeTypeAccessor
        {
            private readonly Func<object> ctor;
            private readonly Func<int, object, object> getter;
            private readonly Dictionary<string, int> map;
            private readonly Action<int, object, object> setter;

            public DelegateAccessor(Dictionary<string, int> map, Func<int, object, object> getter,
                Action<int, object, object> setter, Func<object> ctor, Type type)
            {
                this.map = map;
                this.getter = getter;
                this.setter = setter;
                this.ctor = ctor;
                Type = type;
            }

            protected override Type Type { get; }

            public override bool CreateNewSupported => ctor != null;

            public override object this[object target, string name]
            {
                get
                {
                    int index;
                    if (map.TryGetValue(name, out index)) return getter(index, target);
                    throw new ArgumentOutOfRangeException("name");
                }
                set
                {
                    int index;
                    if (map.TryGetValue(name, out index)) setter(index, target, value);
                    else throw new ArgumentOutOfRangeException("name");
                }
            }

            public override object CreateNew()
            {
                return ctor != null ? ctor() : base.CreateNew();
            }
        }
    }
}
