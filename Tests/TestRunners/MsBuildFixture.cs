﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.Threading;
using Xunit;
using SearchOption = System.IO.SearchOption;

namespace CodeConverter.Tests.TestRunners
{
    /// <summary>
    /// For all files in the testdata folder relevant to the testname, ensures they match the result of the conversion.
    /// Any extra files generated by the conversion are ignored.
    /// Using [Collection(MSBuildFixture.Collection)] will allow this singleton to be injected via the test class constructor.
    /// https://xunit.net/docs/shared-context
    /// </summary>
    [CollectionDefinition(Collection)]
    public class MsBuildFixture : ICollectionFixture<MsBuildFixture>, IDisposable
    {
        public const string Collection = "Uses MSBuild";
        /// <summary>
        /// Leave this set to false when committing.
        /// Commit only the modified files.
        /// </summary>
        private readonly bool _writeNewCharacterization = false;
        /// <summary>
        /// Turn it on to manually check the output loads in VS.
        /// </summary>
        private readonly bool _writeAllFilesForManualTesting = false;

        private readonly Lazy<MSBuildWorkspace> _msBuildWorkspace;
        private readonly AsyncLazy<Solution> _solution;
        private static readonly string OriginalSolutionDir = Path.Combine(GetTestDataDirectory(), "CharacterizationTestSolution");
        private static readonly string SolutionFile = Path.Combine(OriginalSolutionDir, "CharacterizationTestSolution.sln");

        public MsBuildFixture()
        {
            _msBuildWorkspace = new Lazy<MSBuildWorkspace>(CreateWorkspace);
            _solution = new AsyncLazy<Solution>(() => GetSolutionAsync(SolutionFile));
        }

        private async Task<Solution> GetSolutionAsync(string solutionFile)
        {
            var solution = await _msBuildWorkspace.Value.OpenSolutionAsync(solutionFile);
            await AssertMSBuildIsWorkingAndProjectsValid(_msBuildWorkspace.Value.Diagnostics, solution.Projects);
            return solution;
        }

        public void Dispose()
        {
            if (_msBuildWorkspace.IsValueCreated) _msBuildWorkspace.Value.Dispose();
        }

        public async Task ConvertProjectsWhere<TLanguageConversion>(Func<Project, bool> shouldConvertProject, [CallerMemberName] string testName = "") where TLanguageConversion : ILanguageConversion, new()
        {
            var languageNameToConvert = typeof(TLanguageConversion) == typeof(VBToCSConversion)
                ? LanguageNames.VisualBasic
                : LanguageNames.CSharp;

            var projectsToConvert = (await _solution.GetValueAsync()).Projects.Where(p => p.Language == languageNameToConvert && shouldConvertProject(p)).ToArray();
            var conversionResults = (await SolutionConverter.CreateFor<TLanguageConversion>(projectsToConvert).Convert()).ToDictionary(c => c.TargetPathOrNull, StringComparer.OrdinalIgnoreCase);
            var expectedResultDirectory = GetExpectedResultDirectory<TLanguageConversion>(testName);

            try {
                if (!expectedResultDirectory.Exists) expectedResultDirectory.Create();
                var expectedFiles = expectedResultDirectory.GetFiles("*", SearchOption.AllDirectories);
                AssertAllExpectedFilesAreEqual(expectedFiles, conversionResults, expectedResultDirectory, OriginalSolutionDir);
                AssertAllConvertedFilesWereExpected(expectedFiles, conversionResults, expectedResultDirectory, OriginalSolutionDir);
                AssertNoConversionErrors(conversionResults);
            } finally {
                if (_writeNewCharacterization) {
                    if (expectedResultDirectory.Exists) expectedResultDirectory.Delete(true);
                    if (_writeAllFilesForManualTesting) FileSystem.CopyDirectory(OriginalSolutionDir, expectedResultDirectory.FullName);

                    foreach (var conversionResult in conversionResults) {
                        var expectedFilePath =
                            conversionResult.Key.Replace(OriginalSolutionDir, expectedResultDirectory.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(expectedFilePath));
                        File.WriteAllText(expectedFilePath, conversionResult.Value.ConvertedCode);
                    }
                }
            }

            Assert.False(_writeNewCharacterization, $"Test setup issue: Set {nameof(_writeNewCharacterization)} to false after using it");
        }

        private static MSBuildWorkspace CreateWorkspace()
        {
            try {
                return CreateWorkspaceUnhandled();
            } catch (NullReferenceException e) {
                Assert.True(false, "MSBuild nullrefs sometimes, just run the test again." + e);
                return null;
            }
        }

        private static MSBuildWorkspace CreateWorkspaceUnhandled()
        {
            MSBuildLocator.RegisterDefaults();
            return MSBuildWorkspace.Create(new Dictionary<string, string>()
            {
                {"Configuration", "Debug"},
                {"Platform", "AnyCPU"}
            });
        }

        /// <summary>
        /// If you've changed the source project not to compile, the results will be very confusing
        /// If this happens randomly, updating the Microsoft.Build dependency may help - it may have to line up with a version installed on the machine in some way.
        /// </summary>
        private static async Task AssertMSBuildIsWorkingAndProjectsValid(
            ImmutableList<WorkspaceDiagnostic> valueDiagnostics, IEnumerable<Project> projectsToConvert)
        {
            var errors = await projectsToConvert.ParallelSelectAsync(async x => {
                var c = await x.GetCompilationAsync();
                return new[]{CompilationWarnings.WarningsForCompilation(c, c.AssemblyName)}.Concat(
                    valueDiagnostics.Where(d => d.Kind > WorkspaceDiagnosticKind.Warning).Select(d => d.Message));
            }, Env.MaxDop);
            var errorString = string.Join("\r\n", errors.SelectMany(w => w).Where(w => w != null));
            Assert.True(errorString == "", errorString);
        }

        private static void AssertAllConvertedFilesWereExpected(FileInfo[] expectedFiles,
            Dictionary<string, ConversionResult> conversionResults, DirectoryInfo expectedResultDirectory,
            string originalSolutionDir)
        {
            AssertSubset(expectedFiles.Select(f => f.FullName.Replace(expectedResultDirectory.FullName, "")), conversionResults.Select(r => r.Key.Replace(originalSolutionDir, ""))
                    .Where(x => !x.Contains(@"\obj\")),
                "Extra unexpected files were converted");
        }

        private void AssertAllExpectedFilesAreEqual(FileInfo[] expectedFiles, Dictionary<string, ConversionResult> conversionResults,
            DirectoryInfo expectedResultDirectory, string originalSolutionDir)
        {
            foreach (var expectedFile in expectedFiles) {
                AssertFileEqual(conversionResults, expectedResultDirectory, expectedFile, originalSolutionDir);
            }
        }

        private static void AssertNoConversionErrors(Dictionary<string, ConversionResult> conversionResults)
        {
            var errors = conversionResults
                .SelectMany(r => (r.Value.Exceptions ?? new string[0]).Select(e => new { Path = r.Key, Exception = e }))
                .ToList();
            Assert.Empty(errors);
        }

        private static void AssertSubset(IEnumerable<string> superset, IEnumerable<string> subset, string userMessage)
        {
            var notExpected = new HashSet<string>(subset, StringComparer.OrdinalIgnoreCase);
            notExpected.ExceptWith(new HashSet<string>(superset, StringComparer.OrdinalIgnoreCase));
            Assert.False(notExpected.Any(), userMessage + "\r\n" + string.Join("\r\n", notExpected));
        }

        private void AssertFileEqual(Dictionary<string, ConversionResult> conversionResults,
            DirectoryInfo expectedResultDirectory,
            FileInfo expectedFile,
            string actualSolutionDir)
        {
            var convertedFilePath = expectedFile.FullName.Replace(expectedResultDirectory.FullName, actualSolutionDir);
            var fileDidNotNeedConversion = !conversionResults.ContainsKey(convertedFilePath) && File.Exists(convertedFilePath);
            if (fileDidNotNeedConversion) return;

            Assert.True(conversionResults.ContainsKey(convertedFilePath), expectedFile.Name + " is missing from the conversion result of [" + string.Join(",", conversionResults.Keys) + "]");

            var expectedText = File.ReadAllText(expectedFile.FullName);
            var conversionResult = conversionResults[convertedFilePath];
            var actualText = conversionResult.ConvertedCode ?? "" + conversionResult.GetExceptionsAsString() ?? "";

            OurAssert.StringsEqualIgnoringNewlines(expectedText, actualText);
            Assert.Equal(GetEncoding(expectedFile.FullName), GetEncoding(conversionResult));
        }

        private Encoding GetEncoding(ConversionResult conversionResult)
        {
            var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            conversionResult.TargetPathOrNull = filePath;
            conversionResult.WriteToFile();
            var encoding = GetEncoding(filePath);
            File.Delete(filePath);
            return encoding;
        }

        private static Encoding GetEncoding(string filePath)
        {
            using (var reader = new StreamReader(filePath, true)) {
                reader.Peek();
                return reader.CurrentEncoding;
            }
        }

        private static DirectoryInfo GetExpectedResultDirectory<TLanguageConversion>(string testName) where TLanguageConversion : ILanguageConversion, new()
        {
            var combine = Path.Combine(GetTestDataDirectory(), typeof(TLanguageConversion).Name.Replace("Conversion", "Characterization"), testName);
            return new DirectoryInfo(combine);
        }

        private static string GetTestDataDirectory()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var solutionDir = new FileInfo(new Uri(assembly.CodeBase).LocalPath).Directory?.Parent?.Parent?.Parent ??
                              throw new InvalidOperationException(assembly.CodeBase);
            return Path.Combine(solutionDir.FullName, "TestData");
        }
    }
}