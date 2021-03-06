using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Helpers;
using Microsoft.CSS.Core;
using Microsoft.CSS.Editor;
using Microsoft.VisualStudio.Utilities;

namespace MadsKristensen.EditorExtensions
{
    ///<summary>Shared class between Base64Vlq and CssSourceMap</summary>
    public class CssSourceMapNode : SourceMapNode
    {
        public Selector GeneratedSelector { get; set; }
        public Selector OriginalSelector { get; set; }
    }

    ///<summary>CSS source map factory.</summary>
    ///<remarks>
    /// The objects of this class will be instantiated
    /// when LESS or SCSS document is loaded in VS,
    /// and finally get stored in DependencyGraph.
    ///</remarks>
    public sealed class CssSourceMap
    {
        private string _directory;
        private ICssParser _parser;
        private IContentType _contentType;

        public IEnumerable<CssSourceMapNode> MapNodes { get; private set; }

        private CssSourceMap()
        { }

        public CssSourceMap(string targetFileName, string mapFileName, IContentType contentType)
        {
            Initialize(targetFileName, mapFileName, contentType);
        }

        private void Initialize(string targetFileName, string mapFileName, IContentType contentType)
        {
            _contentType = contentType;
            _parser = CssParserLocator.FindComponent(_contentType).CreateParser();
            _directory = Path.GetDirectoryName(mapFileName);
            PopulateMap(targetFileName, mapFileName); // Begin two-steps initialization.
        }

        private void PopulateMap(string targetFileName, string mapFileName)
        {
            var map = new SourceMapDefinition();

            try
            {
                map = Json.Decode<SourceMapDefinition>(File.ReadAllText(mapFileName));
            }
            catch
            {
                return;
            }

            MapNodes = Base64Vlq.Decode(map.mappings, _directory, map.sources);

            if (MapNodes.Count() == 0)
                return;

            CollectRules(targetFileName);
        }

        private void CollectRules(string targetFileName)
        {
            // Sort collection for source file.
            MapNodes = MapNodes.OrderBy(x => x.SourceFilePath)
                               .ThenBy(x => x.OriginalLine)
                               .ThenBy(x => x.OriginalColumn);

            if (_contentType.DisplayName.Equals("SCSS", StringComparison.OrdinalIgnoreCase))
                MapNodes = CorrectionsForScss(File.ReadAllText(targetFileName));

            MapNodes = ProcessSourceMaps();

            // Sort collection for generated file.
            MapNodes = MapNodes.OrderBy(x => x.GeneratedLine)
                               .ThenBy(x => x.GeneratedColumn);

            MapNodes = ProcessGeneratedMaps(File.ReadAllText(targetFileName));

            MapNodes.Any();
        }

        // A very ugly hack for a very ugly bug: https://github.com/hcatlin/libsass/issues/324
        // Remove this and its caller in previous method, when it is fixed in original repo
        // and https://github.com/andrew/node-sass/ is released with the fix.
        // Overwriting all positions belonging to original/source file.
        private IEnumerable<CssSourceMapNode> CorrectionsForScss(string cssFileContents)
        {
            // Sort collection for generated file.
            var sortedForGenerated = MapNodes.OrderBy(x => x.GeneratedLine)
                                             .ThenBy(x => x.GeneratedColumn)
                                             .ToList();

            ParseItem item = null;
            Selector selector = null;
            StyleSheet styleSheet = null, cssStyleSheet = null;
            int start = 0, indexInCollection, targetDepth;
            string fileContents = null;
            var result = new List<CssSourceMapNode>();
            var contentCollection = new Dictionary<string, string>(); // So we don't have to read file for each map item.
            var parser = new CssParser();

            cssStyleSheet = parser.Parse(cssFileContents, false);

            foreach (var node in MapNodes)
            {
                // Cache source file contents.
                if (!contentCollection.ContainsKey(node.SourceFilePath))
                {
                    if (!File.Exists(node.SourceFilePath)) // Lets say someone deleted the reference file.
                        continue;

                    fileContents = File.ReadAllText(node.SourceFilePath);

                    contentCollection.Add(node.SourceFilePath, fileContents);

                    styleSheet = _parser.Parse(fileContents, false);
                }

                start = cssFileContents.NthIndexOfCharInString('\n', node.GeneratedLine);
                start += node.GeneratedColumn;

                item = cssStyleSheet.ItemAfterPosition(start);
                selector = item.FindType<Selector>();

                if (selector == null)
                    continue;

                indexInCollection = sortedForGenerated.FindIndex(e => e.Equals(node));//sortedForGenerated.IndexOf(node);

                targetDepth = 1;

                for (int i = indexInCollection;
                     i >= 0 && node.GeneratedLine == sortedForGenerated[i].GeneratedLine;
                     targetDepth++, --i) ;

                start = fileContents.NthIndexOfCharInString('\n', node.OriginalLine);
                start += node.OriginalColumn;

                item = styleSheet.ItemAfterPosition(start);

                while (item.TreeDepth > targetDepth)
                {
                    item = item.Parent;
                }

                selector = item.FindType<RuleSet>().Selectors.First();

                if (selector == null)
                    continue;

                node.OriginalLine = fileContents.Substring(0, selector.Start).Count(s => s == '\n');
                node.OriginalColumn = fileContents.GetLineColumn(selector.Start, node.OriginalLine);

                result.Add(node);
            }

            return result;
        }

        private IEnumerable<CssSourceMapNode> ProcessSourceMaps()
        {
            ParseItem item = null;
            Selector selector = null;
            StyleSheet styleSheet = null;
            int start;
            var fileContents = "";
            var result = new List<CssSourceMapNode>();
            var contentCollection = new Dictionary<string, string>(); // So we don't have to read file for each map item.

            foreach (var node in MapNodes)
            {
                // Cache source file contents.
                if (!contentCollection.ContainsKey(node.SourceFilePath))
                {
                    if (!File.Exists(node.SourceFilePath)) // Lets say someone deleted the reference file.
                        continue;

                    fileContents = File.ReadAllText(node.SourceFilePath);

                    contentCollection.Add(node.SourceFilePath, fileContents);

                    styleSheet = _parser.Parse(fileContents, false);
                }

                start = fileContents.NthIndexOfCharInString('\n', node.OriginalLine);
                start += node.OriginalColumn;

                item = styleSheet.ItemAfterPosition(start);

                selector = item.FindType<Selector>();

                if (selector == null)
                    continue;

                node.OriginalSelector = selector;

                result.Add(node);
            }

            return result;
        }

        private IEnumerable<CssSourceMapNode> ProcessGeneratedMaps(string fileContents)
        {
            ParseItem item = null;
            Selector selector = null;
            StyleSheet styleSheet = null;
            SimpleSelector simple = null;
            int start;
            var parser = new CssParser();
            var result = new List<CssSourceMapNode>();

            styleSheet = parser.Parse(fileContents, false);

            foreach (var node in MapNodes)
            {
                start = fileContents.NthIndexOfCharInString('\n', node.GeneratedLine);
                start += node.GeneratedColumn;

                item = styleSheet.ItemAfterPosition(start);
                selector = item.FindType<Selector>();

                if (selector == null)
                    continue;

                var depth = node.OriginalSelector.TreeDepth / 2;

                if (depth < selector.SimpleSelectors.Count)
                {
                    simple = item.FindType<SimpleSelector>();

                    if (simple == null)
                        simple = item.Parent.FindType<SimpleSelector>();

                    var selectorText = new StringBuilder();

                    for (int i = 0; i < node.OriginalSelector.SimpleSelectors.Count; i++)
                    {

                        selectorText.Append(simple.Text).Append(" ");
                        simple = simple.NextSibling as SimpleSelector;
                    }

                    selector = parser.Parse(selectorText.ToString(), false).RuleSets.First().Selectors.First() as Selector;

                    if (selector == null)
                        continue;
                }

                node.GeneratedSelector = selector;

                result.Add(node);
            }

            return result;
        }

        private struct SourceMapDefinition
        {
            public string file;
            public string mappings;
            public string[] sources;
        }
    }
}