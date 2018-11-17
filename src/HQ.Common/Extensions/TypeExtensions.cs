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
using System.Linq;
using System.Reflection;

namespace HQ.Common.Extensions
{
    public static class TypeExtensions
    {
        public static ConstructorInfo GetWidestConstructor(this Type type)
        {
            var allPublic = type.GetConstructors();
            var constructor = allPublic.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            return constructor ?? type.GetConstructor(Type.EmptyTypes);
        }

        public static MethodInfo GetWidestMethod(this Type type, string name,
            StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            var allPublic = type.GetMethods().Where(m => m.Name.Equals(name, comparison));
            var method = allPublic.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            return method ?? type.GetMethod(name);
        }

        public static string GetNonGenericName(this Type type)
        {
            if (type == null)
                return null;
            var name = type.Name;
            var index = name.IndexOf('`');
            return index == -1 ? name : name.Substring(0, index);
        }
    }
}
