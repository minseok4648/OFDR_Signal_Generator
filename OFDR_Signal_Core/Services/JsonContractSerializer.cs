using System.IO;
using System.Runtime.Serialization.Json;

namespace OFDR_Signal_Core.Services
{
    public static class JsonContractSerializer
    {
        public static void Write<T>(string path, T value)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.Create(path)) serializer.WriteObject(stream, value);
        }

        public static T Read<T>(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.OpenRead(path)) return (T)serializer.ReadObject(stream);
        }
    }
}
