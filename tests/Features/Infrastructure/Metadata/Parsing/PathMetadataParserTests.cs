/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text.Json;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Parsing
{
    public class PathMetadataParserTests
    {
        [Fact]
        public void Parse_UsesResolvedCaseSensitivityForContainment()
        {
            var root = Path.Join(Path.GetTempPath(), $"MetadataRoot-{Guid.NewGuid():N}");
            var differentlyCasedRoot = root.ToUpperInvariant();
            var file = Path.Join(root, "Author", "2020 - Title", "book.m4b");
            var syntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;

            var sensitive = PathMetadataParser.Parse(
                file,
                differentlyCasedRoot,
                new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Sensitive));
            var insensitive = PathMetadataParser.Parse(
                file,
                differentlyCasedRoot,
                new FileSystemPathSemantics(syntax, FileSystemCaseSensitivity.Insensitive));

            Assert.Null(sensitive.Title);
            Assert.Equal("Title", insensitive.Title);
            Assert.Equal("Author", insensitive.Author);
        }

        [Fact]
        public async Task ReadEmbeddedTagsAsync_CanceledToken_PropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PathMetadataParser.ReadEmbeddedTagsAsync(
                    "book.m4b",
                    "ffprobe-does-not-need-to-exist-for-canceled-work",
                    cancellation.Token));
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesStandardAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "album_artist": "SenLinYu",
                  "ASIN": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("Alchemised", result.Title);
            Assert.Equal("SenLinYu", result.Author);
            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Theory]
        [InlineData("Vor aller Augen [B0FPXP67JW]", "B0FPXP67JW")]
        [InlineData("Feuermond [B0B1QRFPHB]/Feuermond: Die drei ???, Folge 125 [B0B1QRFPHB].m4b", "B0B1QRFPHB")]
        [InlineData(@"C:\Books\Adrenalin [B09VPYXHRC]\book.m4b", "B09VPYXHRC")]
        [InlineData("Achtsam morden [383714710X]", null)] // ISBN-like, not an Audible ASIN
        [InlineData("Plain Title Without Identifier", null)]
        [InlineData("Series [Stormlight 1]", null)] // bracket content is not an ASIN
        [InlineData("", null)]
        [InlineData("Vor aller Augen B0FPXP67JW", "B0FPXP67JW")] // unbracketed folder token
        [InlineData("B0FPXP67JW - Vor aller Augen", "B0FPXP67JW")] // leading token with dash separator
        [InlineData("/books/B0FPXP67JW/book.m4b", "B0FPXP67JW")] // ASIN as folder name itself
        [InlineData("/books/Feuermond/Feuermond B0B1QRFPHB.m4b", "B0B1QRFPHB")] // unbracketed file token
        [InlineData("/books/Feuermond B0FPXP67JW/file.m4b", "B0FPXP67JW")] // unbracketed folder segment
        [InlineData("Achtsam morden 383714710X", null)] // unbracketed ISBN-like token must not match
        public void ExtractAsinFromPath_StrictAsinAnywhereInPath(string path, string? expected)
        {
            Assert.Equal(expected, PathMetadataParser.ExtractAsinFromPath(path));
        }

        [Fact]
        public void ExtractAsinFromPath_PrefersFirstOccurrence()
        {
            var path = "/books/Series [B0AAAAAAA1]/Title [B0BBBBBBB2].m4b";
            Assert.Equal("B0AAAAAAA1", PathMetadataParser.ExtractAsinFromPath(path));
        }

        [Fact]
        public void ExtractAsinFromPath_TrailingSeparator_TreatedAsFolder()
        {
            var path = "/books/Vor aller Augen [B0FPXP67JW]/";
            Assert.Equal("B0FPXP67JW", PathMetadataParser.ExtractAsinFromPath(path));
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesMp3UserTextAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "TXXX:ASIN": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesAppleFreeformAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "----:com.apple.iTunes:ASIN": "amazon://asin/B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesColonSuffixedAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "ASIN:": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesCdekTagContainingAsin()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "CDEK:": "amazon://asin/B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }
    }
}
