using System.Collections.Generic;
using Mutagen.Bethesda.Synthesis.Settings;

namespace AllGUD
{
    public class Settings
    {
        [SynthesisSettingName("Minimum Immersive Equipment Display version (display only):")]
        public string immersiveEquipmentDisplayVersion { get; } = "1.1.1";

        [SynthesisSettingName("Diagnostics")]
        public Diagnostics diagnostics { get; set; } = new Diagnostics();

        [SynthesisSettingName("Meshes for Weapons")]
        public Meshes meshes { get; set; } = new Meshes();

        public List<string> GetConfigErrors()
        {
            var errors = diagnostics.GetConfigErrors();
            errors.AddRange(meshes.GetConfigErrors());
            return errors;
        }
    }
}
