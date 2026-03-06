using System;
using System.Linq;
using System.Reflection;
using Vortice.Direct3D11;
using Vortice.DXGI;

void Dump(Type t)
{
    Console.WriteLine($"TYPE {t.FullName}");
    foreach (var ctor in t.GetConstructors()) Console.WriteLine("CTOR " + ctor);
    foreach (var p in t.GetProperties(BindingFlags.Public|BindingFlags.Instance)) Console.WriteLine("PROP " + p.PropertyType.Name + " " + p.Name);
    foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.Instance)) Console.WriteLine("FIELD " + f.FieldType.Name + " " + f.Name);
    Console.WriteLine();
}

Dump(typeof(ShaderResourceViewDescription));
Dump(typeof(UnorderedAccessViewDescription));
Dump(typeof(Texture2DArrayShaderResourceView));
Dump(typeof(Texture2DArrayUnorderedAccessView));
