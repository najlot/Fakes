using Najlot.Fakes.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Najlot.Fakes.Attributes;

namespace Najlot.Fakes.Tests;

[TestClass]
public sealed class GeneratedFakeFormattingTests
{
	[TestMethod]
	public void Generated_Source_Uses_Expected_Indentation()
	{
		const string source = """
using Najlot.Fakes.Attributes;

namespace Formatting.Sample;

public interface IFormatter
{
	int Format(out string text);
}

[Fake]
internal partial class FormatterFake : IFormatter
{
}
""";

		var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
		var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Select(static path => MetadataReference.CreateFromFile(path))
			.Concat([MetadataReference.CreateFromFile(typeof(FakeAttribute).Assembly.Location)]);

		var compilation = CSharpCompilation.Create(
			"FormattingTests",
			[syntaxTree],
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		GeneratorDriver driver = CSharpGeneratorDriver.Create(new FakeGenerator());
		driver = driver.RunGenerators(compilation);

		var generatedSource = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString().Replace("\r\n", "\n");

		StringAssert.Contains(generatedSource, "    internal partial class FormatterFake\n    {\n");
		StringAssert.Contains(generatedSource, "            _formatHandler = delegate(out global::System.String text)\n");
		StringAssert.Contains(generatedSource, "            {\n");
		StringAssert.Contains(generatedSource, "                text = default!;\n");
		StringAssert.Contains(generatedSource, "                return value;\n");
		StringAssert.Contains(generatedSource, "            };\n");
		Assert.IsTrue(generatedSource.EndsWith("    }\n\n}\n"));
		Assert.IsFalse(generatedSource.Contains("internal partial class FormatterFake\n    \n    {"));
		Assert.IsFalse(generatedSource.EndsWith("    }\n"));
	}
}