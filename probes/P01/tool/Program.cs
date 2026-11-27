using System;
using System.Reflection;

if (args is not ["--probe", "two words"])
{
    Console.Error.WriteLine("Expected --probe followed by a single 'two words' argument.");
    return 2;
}

var assembly = Assembly.GetExecutingAssembly();
Console.WriteLine(assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
return 0;
