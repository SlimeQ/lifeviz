using System;
using System.Linq;
using System.Reflection;
using Vortice.Direct3D11;

void Dump(Type t)
{
    Console.WriteLine($"TYPE {t.FullName}");
    foreach (var ctor in t.GetConstructors()) Console.WriteLine($"CTOR {ctor}");
    foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"PROP {prop.PropertyType.FullName} {prop.Name}");
    foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"FIELD {field.FieldType.FullName} {field.Name}");
    Console.WriteLine();
}
Dump(typeof(ShaderResourceViewDescription));
Dump(typeof(UnorderedAccessViewDescription));
Dump(typeof(Texture2DArrayShaderResourceView));
Dump(typeof(Texture2DArrayUnorderedAccessView));
