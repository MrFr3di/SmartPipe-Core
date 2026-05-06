// <copyright file="LargeBenignInputs.cs" company="SmartPipe">
// Copyright (c) SmartPipe. All rights reserved.
// </copyright>

using System.Text;

namespace SmartPipe.Core.Tests.Infrastructure.TestCases;

/// <summary>
/// Large benign input test cases for false positive validation.
/// Generates synthetic data of various sizes (1MB, 5MB, 10MB) in different formats
/// containing NO secret patterns. Used to verify the secret scanner does not produce
/// false positives on large legitimate inputs.
/// </summary>
public static class LargeBenignInputs
{
    /// <summary>
    /// Generates 1MB of lorem ipsum text.
    /// </summary>
    public static string Generate1MbLoremIpsum() => GenerateLoremIpsum(1_000_000);

    /// <summary>
    /// Generates 5MB of lorem ipsum text.
    /// </summary>
    public static string Generate5MbLoremIpsum() => GenerateLoremIpsum(5_000_000);

    /// <summary>
    /// Generates 10MB of lorem ipsum text.
    /// </summary>
    public static string Generate10MbLoremIpsum() => GenerateLoremIpsum(10_000_000);

    /// <summary>
    /// Generates 1MB of synthetic JSON data.
    /// </summary>
    public static string Generate1MbJson() => GenerateJson(1_000_000);

    /// <summary>
    /// Generates 5MB of synthetic JSON data.
    /// </summary>
    public static string Generate5MbJson() => GenerateJson(5_000_000);

    /// <summary>
    /// Generates 10MB of synthetic JSON data.
    /// </summary>
    public static string Generate10MbJson() => GenerateJson(10_000_000);

    /// <summary>
    /// Generates 1MB of synthetic XML data.
    /// </summary>
    public static string Generate1MbXml() => GenerateXml(1_000_000);

    /// <summary>
    /// Generates 5MB of synthetic XML data.
    /// </summary>
    public static string Generate5MbXml() => GenerateXml(5_000_000);

    /// <summary>
    /// Generates 10MB of synthetic XML data.
    /// </summary>
    public static string Generate10MbXml() => GenerateXml(10_000_000);

    /// <summary>
    /// Generates 1MB of synthetic C# code.
    /// </summary>
    public static string Generate1MbCSharpCode() => GenerateCSharpCode(1_000_000);

    /// <summary>
    /// Generates 5MB of synthetic C# code.
    /// </summary>
    public static string Generate5MbCSharpCode() => GenerateCSharpCode(5_000_000);

    /// <summary>
    /// Generates 10MB of synthetic C# code.
    /// </summary>
    public static string Generate10MbCSharpCode() => GenerateCSharpCode(10_000_000);

    /// <summary>
    /// Generates lorem ipsum text of approximately the specified size in bytes.
    /// Uses standard lorem ipsum text with no secret patterns.
    /// </summary>
    /// <param name="targetSizeBytes">Target size in bytes</param>
    /// <returns>Lorem ipsum text of approximately targetSizeBytes</returns>
    private static string GenerateLoremIpsum(int targetSizeBytes)
    {
        // Standard lorem ipsum paragraphs - completely benign, no secrets
        var paragraphs = new[]
        {
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.",
            "Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.",
            "Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium doloremque laudantium, totam rem aperiam, eaque ipsa quae ab illo inventore veritatis et quasi architecto beatae vitae dicta sunt explicabo.",
            "Nemo enim ipsam voluptatem quia voluptas sit aspernatur aut odit aut fugit, sed quia consequuntur magni dolores eos qui ratione voluptatem sequi nesciunt. Neque porro quisquam est, qui dolorem ipsum quia dolor sit amet.",
            "At vero eos et accusamus et iusto odio dignissimos ducimus qui blanditiis praesentium voluptatum deleniti atque corrupti quos dolores et quas molestias excepturi sint occaecati cupiditate non provident.",
            "Similique sunt in culpa qui officia deserunt mollitia animi, id est laborum et dolorum fuga. Et harum quidem rerum facilis est et expedita distinctio. Nam libero tempore, cum soluta nobis est eligendi optio.",
            "Cumque nihil impedit quo minus id quod maxime placeat facere possimus, omnis voluptas assumenda est, omnis dolor repellendus. Temporibus autem quibusdam et aut officiis debitis aut rerum necessitatibus.",
            "Itaque earum rerum hic tenetur a sapiente delectus, ut aut reiciendis voluptatibus maiores alias consequatur aut perferendis doloribus asperiores repellat. Integer posuere erat a ante venenatis dapibus posuere velit aliquet."
        };

        var sb = new StringBuilder(targetSizeBytes);
        int paragraphIndex = 0;

        while (sb.Length < targetSizeBytes)
        {
            sb.Append(paragraphs[paragraphIndex]);
            sb.Append(" ");
            paragraphIndex = (paragraphIndex + 1) % paragraphs.Length;

            // Add line breaks periodically for realism
            if (paragraphIndex == 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }
        }

        // Trim to exact target size if we overshot
        if (sb.Length > targetSizeBytes)
        {
            sb.Length = targetSizeBytes;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates synthetic JSON data of approximately the specified size.
    /// Contains realistic but fake data structures with no secret values.
    /// </summary>
    /// <param name="targetSizeBytes">Target size in bytes</param>
    /// <returns>JSON string of approximately targetSizeBytes</returns>
    private static string GenerateJson(int targetSizeBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"metadata\": {");
        sb.AppendLine("    \"version\": \"1.0.0\",");
        sb.AppendLine("    \"generated\": \"2026-04-30T00:00:00Z\",");
        sb.AppendLine("    \"description\": \"Synthetic test data for false positive validation\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"records\": [");

        int recordIndex = 0;
        var recordTemplate = @"    {{
      ""id"": {0},
      ""name"": ""Record_{0}"",
      ""category"": ""Category_{1}"",
      ""description"": ""This is a synthetic record used for testing purposes. It contains no sensitive information or secret patterns. The data is entirely fictional and generated for validation of security scanning tools."",
      ""attributes"": {{
        ""field_a"": ""value_{2}"",
        ""field_b"": ""data_{3}"",
        ""field_c"": ""item_{4}"",
        ""status"": ""active"",
        ""priority"": {5}
      }},
      ""tags"": [""tag1"", ""tag2"", ""tag3""],
      ""metadata"": {{
        ""created"": ""2026-01-01T00:00:00Z"",
        ""modified"": ""2026-04-30T00:00:00Z"",
        ""version"": 1
      }}
    }}";

        while (sb.Length < targetSizeBytes)
        {
            int catIndex = recordIndex % 10;
            int priority = (recordIndex % 5) + 1;

            sb.AppendFormat(recordTemplate,
                recordIndex,
                catIndex,
                recordIndex,
                recordIndex,
                recordIndex,
                priority);

            recordIndex++;

            if (sb.Length < targetSizeBytes)
            {
                sb.AppendLine(",");
            }
            else
            {
                sb.AppendLine();
            }
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");

        // Trim to exact target size if needed
        if (sb.Length > targetSizeBytes)
        {
            sb.Length = targetSizeBytes;
            // Ensure valid JSON by closing properly
            if (!sb.ToString().TrimEnd().EndsWith("}"))
            {
                // Find last complete record and close properly
                int lastComma = sb.ToString().LastIndexOf(',');
                if (lastComma > 0)
                {
                    sb.Length = lastComma;
                    sb.AppendLine();
                    sb.AppendLine("  ]");
                    sb.AppendLine("}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates synthetic XML data of approximately the specified size.
    /// Contains realistic but fake data structures with no secret values.
    /// </summary>
    /// <param name="targetSizeBytes">Target size in bytes</param>
    /// <returns>XML string of approximately targetSizeBytes</returns>
    private static string GenerateXml(int targetSizeBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<dataset>");
        sb.AppendLine("  <metadata>");
        sb.AppendLine("    <version>1.0.0</version>");
        sb.AppendLine("    <generated>2026-04-30T00:00:00Z</generated>");
        sb.AppendLine("    <description>Synthetic test data for false positive validation</description>");
        sb.AppendLine("  </metadata>");
        sb.AppendLine("  <records>");

        int recordIndex = 0;

        while (sb.Length < targetSizeBytes)
        {
            int categoryIndex = recordIndex % 10;
            int priority = (recordIndex % 5) + 1;

            sb.AppendLine("    <record>");
            sb.AppendLine($"      <id>{recordIndex}</id>");
            sb.AppendLine($"      <name>Record_{recordIndex}</name>");
            sb.AppendLine($"      <category>Category_{categoryIndex}</category>");
            sb.AppendLine("      <description>This is a synthetic record used for testing purposes. It contains no sensitive information or secret patterns. The data is entirely fictional and generated for validation of security scanning tools.</description>");
            sb.AppendLine("      <attributes>");
            sb.AppendLine($"        <field_a>value_{recordIndex}</field_a>");
            sb.AppendLine($"        <field_b>data_{recordIndex}</field_b>");
            sb.AppendLine($"        <field_c>item_{recordIndex}</field_c>");
            sb.AppendLine("        <status>active</status>");
            sb.AppendLine($"        <priority>{priority}</priority>");
            sb.AppendLine("      </attributes>");
            sb.AppendLine("      <tags>");
            sb.AppendLine("        <tag>tag1</tag>");
            sb.AppendLine("        <tag>tag2</tag>");
            sb.AppendLine("        <tag>tag3</tag>");
            sb.AppendLine("      </tags>");
            sb.AppendLine("      <metadata>");
            sb.AppendLine("        <created>2026-01-01T00:00:00Z</created>");
            sb.AppendLine("        <modified>2026-04-30T00:00:00Z</modified>");
            sb.AppendLine("        <version>1</version>");
            sb.AppendLine("      </metadata>");
            sb.AppendLine("    </record>");

            recordIndex++;
        }

        sb.AppendLine("  </records>");
        sb.AppendLine("</dataset>");

        // Trim to exact target size if needed
        if (sb.Length > targetSizeBytes)
        {
            sb.Length = targetSizeBytes;
            // Ensure well-formed XML by closing properly
            string current = sb.ToString();
            if (!current.TrimEnd().EndsWith("</dataset>"))
            {
                int lastRecordEnd = current.LastIndexOf("</record>");
                if (lastRecordEnd > 0)
                {
                    sb.Length = lastRecordEnd + "</record>".Length;
                    sb.AppendLine();
                    sb.AppendLine("  </records>");
                    sb.AppendLine("</dataset>");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates synthetic C# code of approximately the specified size.
    /// Contains realistic but fake code with no secret values or credentials.
    /// </summary>
    /// <param name="targetSizeBytes">Target size in bytes</param>
    /// <returns>C# code string of approximately targetSizeBytes</returns>
    private static string GenerateCSharpCode(int targetSizeBytes)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <copyright file=\"GeneratedCode.cs\" company=\"TestCompany\">");
        sb.AppendLine("// Copyright (c) TestCompany. All rights reserved.");
        sb.AppendLine("// </copyright>");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine("namespace TestCompany.GeneratedCode");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Generated test class for false positive validation.");
        sb.AppendLine("    /// This code is synthetic and contains no sensitive information.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static class GeneratedTestClass");
        sb.AppendLine("    {");

        int methodIndex = 0;
        var methodNames = new[] { "Process", "Validate", "Transform", "Calculate", "Analyze", "Execute", "Handle", "Manage", "Generate", "Build" };
        var paramNames = new[] { "input", "data", "value", "item", "entity", "model", "context", "request", "config", "options" };
        var returnTypes = new[] { "int", "string", "bool", "DateTime", "List<string>", "Dictionary<string, object>", "Result", "Response", "Output", "Status" };

        while (sb.Length < targetSizeBytes)
        {
            string methodName = methodNames[methodIndex % methodNames.Length];
            string paramName = paramNames[methodIndex % paramNames.Length];
            string returnType = returnTypes[methodIndex % returnTypes.Length];
            int complexity = (methodIndex % 3) + 1;

            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// {methodName}s the specified {paramName}.");
            sb.AppendLine($"        /// This is a generated method for testing purposes.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        /// <param name=\"{paramName}\">The {paramName} to process</param>");
            sb.AppendLine($"        /// <returns>A {returnType} result</returns>");
            sb.AppendLine($"        public static {returnType} {methodName}{methodIndex}({returnType} {paramName})");
            sb.AppendLine("        {");

            // Generate method body based on complexity
            for (int i = 0; i < complexity; i++)
            {
                sb.AppendLine($"            // Processing step {i + 1} for {methodName}{methodIndex}");
                sb.AppendLine($"            var temp{i} = {paramName};");
                sb.AppendLine($"            // Additional processing logic here");
                sb.AppendLine($"            // This is synthetic code with no secrets");
            }

            sb.AppendLine($"            return {paramName};");
            sb.AppendLine("        }");
            sb.AppendLine();

            methodIndex++;

            // Add property every 5 methods
            if (methodIndex % 5 == 0)
            {
                string propType = returnTypes[methodIndex % returnTypes.Length];
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// Gets the generated property {methodIndex}.");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        public static {propType} Property{methodIndex} {{ get; }} = default;");
                sb.AppendLine();
            }

            // Add nested class every 15 methods
            if (methodIndex % 15 == 0 && methodIndex > 0)
            {
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// Nested helper class {methodIndex / 15}.");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        public static class HelperClass{methodIndex / 15}");
                sb.AppendLine("        {");
                sb.AppendLine($"            /// <summary>");
                sb.AppendLine($"            /// Helper method for processing.");
                sb.AppendLine($"            /// </summary>");
                sb.AppendLine($"            public static void HelperMethod{methodIndex / 15}()");
                sb.AppendLine("            {");
                sb.AppendLine("                // Helper logic here");
                sb.AppendLine("                // No sensitive data or secrets");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Trim to exact target size if needed
        if (sb.Length > targetSizeBytes)
        {
            sb.Length = targetSizeBytes;
            // Ensure we end with a closing brace
            string current = sb.ToString();
            if (!current.TrimEnd().EndsWith("}"))
            {
                // Find last complete method and close properly
                int lastMethodEnd = current.LastIndexOf("        }");
                if (lastMethodEnd > 0)
                {
                    sb.Length = lastMethodEnd + "        }".Length;
                    sb.AppendLine();
                    sb.AppendLine("    }");
                    sb.AppendLine("}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets all large benign input generators as a dictionary.
    /// </summary>
    /// <returns>Dictionary mapping descriptive names to generator functions</returns>
    public static Dictionary<string, Func<string>> GetAllGenerators() => new()
    {
        ["1MB Lorem Ipsum"] = Generate1MbLoremIpsum,
        ["5MB Lorem Ipsum"] = Generate5MbLoremIpsum,
        ["10MB Lorem Ipsum"] = Generate10MbLoremIpsum,
        ["1MB JSON"] = Generate1MbJson,
        ["5MB JSON"] = Generate5MbJson,
        ["10MB JSON"] = Generate10MbJson,
        ["1MB XML"] = Generate1MbXml,
        ["5MB XML"] = Generate5MbXml,
        ["10MB XML"] = Generate10MbXml,
        ["1MB C# Code"] = Generate1MbCSharpCode,
        ["5MB C# Code"] = Generate5MbCSharpCode,
        ["10MB C# Code"] = Generate10MbCSharpCode,
    };

    /// <summary>
    /// Gets all large benign input test cases as MemberData-compatible enumerable.
    /// Each item contains: size category, data type, and the generated input string.
    /// </summary>
    /// <returns>Enumerable of test case data arrays for xUnit MemberData</returns>
    public static IEnumerable<object[]> GetAllTestCases()
    {
        foreach (var kvp in GetAllGenerators())
        {
            var parts = kvp.Key.Split(' ', 2);
            var size = parts[0];
            var type = parts[1];
            yield return new object[] { size, type, kvp.Value() };
        }
    }

    /// <summary>
    /// Gets the approximate size in bytes for each generator category.
    /// </summary>
    public static IReadOnlyDictionary<string, int> SizeCategories => new Dictionary<string, int>
    {
        ["1MB"] = 1_000_000,
        ["5MB"] = 5_000_000,
        ["10MB"] = 10_000_000,
    };
}