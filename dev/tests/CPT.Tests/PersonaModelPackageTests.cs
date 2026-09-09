using CPT.Core.Models;
using CPT.Core.Personas;
using Xunit;

namespace CPT.Tests;

public sealed class PersonaModelPackageTests
{
    [Fact]
    public void Model_and_framing_survive_package_without_original_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "cpt-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = Path.Combine(root, "source.glb");
            byte[] data = [103, 108, 84, 70, 2, 0, 0, 0];
            File.WriteAllBytes(model, data);
            var persona = new Persona { Id = "model-test", Name = "Model test", SystemPrompt = "Preserve me",
                Visual = new VisualConfig { HologramStyle = "steam", ModelFile = model, ModelFraming = "upper", ModelHeadFraction = 0.3, ModelRotation = 90, ModelZoom = 1.2 } };
            var package = Path.Combine(root, "test.cptpersona");
            PersonaPackage.Export(persona, package);
            File.Delete(model);
            var imported = PersonaPackage.Import(package, new PersonaStore(Path.Combine(root, "personas")));
            Assert.Equal(data, File.ReadAllBytes(imported.Visual.ModelFile!));
            Assert.Equal("steam", imported.Visual.HologramStyle);
            Assert.Equal("upper", imported.Visual.ModelFraming);
            Assert.Equal(0.3, imported.Visual.ModelHeadFraction);
            Assert.Equal(90, imported.Visual.ModelRotation);
            Assert.Equal(1.2, imported.Visual.ModelZoom);
            Assert.Equal("Preserve me", imported.SystemPrompt);
        }
        finally { Directory.Delete(root, true); }
    }
}
