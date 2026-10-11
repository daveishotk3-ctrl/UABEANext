using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace AssetsTools.NET.Cpp2IL
{
    internal static class Extensions
    {
        public static Il2CppTypeDefinition Resolve(this Il2CppTypeReflectionData typeRef)
        {
            return typeRef.isArray ? Resolve(typeRef.arrayType) : typeRef.baseType;
        }

        public static Il2CppTypeReflectionData GetEnumUnderlyingType(this Il2CppTypeDefinition self)
        {
            // Fields and FieldAttributes build a new array every time they are read, so only read them once
            Il2CppFieldDefinition[] fields = self.Fields;
            FieldAttributes[] fieldAttributes = self.FieldAttributes;
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fieldAttributes[i].HasFlag(FieldAttributes.Static))
                {
                    return fields[i].FieldType;
                }
            }

            throw new ArgumentException();
        }
    }
}
