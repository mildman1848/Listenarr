from pathlib import Path

source = Path("listenarr.infrastructure/FileSystem/FileMover.cs")
destination = Path("listenarr.infrastructure/FileSystem/FileMover.Move.cs")
text = source.read_text(encoding="utf-8")
if destination.exists():
    raise RuntimeError(f"Destination already exists: {destination}")

anchor = "        public async Task<bool> MoveFileAsync(string sourceFile, string destFile)\n"
start = text.find(anchor)
if start < 0:
    raise RuntimeError("MoveFileAsync anchor was not found")

closing = "\n    }\n}\n"
end = text.rfind(closing)
if end < start:
    raise RuntimeError("FileMover class closing braces were not found after MoveFileAsync")

method_block = text[start:end]
source.write_text(text[:start] + closing.lstrip("\n"), encoding="utf-8")
destination.write_text(
    """/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Runtime.InteropServices;
using System.Security.Principal;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public partial class FileMover
    {
""" + method_block + "\n    }\n}\n",
    encoding="utf-8",
)
