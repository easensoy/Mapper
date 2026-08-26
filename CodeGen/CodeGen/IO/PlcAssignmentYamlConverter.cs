using System;
using CodeGen.Translation;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace CodeGen.Configuration
{
    // `plc: M262` is a NAME, and a target's name is whatever device.yml calls it. Reading it as a name
    // is what makes the target set open: it used to be an enum, so a declaration could only name a
    // target some C# already knew, and anything else silently read as "no target".
    internal sealed class PlcAssignmentYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(PlcAssignment);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = parser.Consume<Scalar>();
            return PlcAssignment.Named(scalar.Value);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            emitter.Emit(new Scalar(((PlcAssignment?)value)?.Name ?? string.Empty));
    }
}
