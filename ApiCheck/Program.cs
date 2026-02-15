using System;
using System.Reflection;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Abstractions;

class Program
{
    static void Main()
    {
        Console.WriteLine("=== VideoSample Properties ===");
        var type = typeof(VideoSample);
        foreach (var prop in type.GetProperties())
        {
            Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name}");
        }
        foreach (var field in type.GetFields())
        {
            Console.WriteLine($"  (field) {field.FieldType.Name} {field.Name}");
        }

        Console.WriteLine("\n=== RawImage Properties ===");
        var rawType = typeof(RawImage);
        foreach (var prop in rawType.GetProperties())
        {
            Console.WriteLine($"  {prop.PropertyType.Name} {prop.Name}");
        }
        foreach (var field in rawType.GetFields())
        {
            Console.WriteLine($"  (field) {field.FieldType.Name} {field.Name}");
        }
    }
}
