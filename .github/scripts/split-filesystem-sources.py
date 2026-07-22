from pathlib import Path


def split_once(path: Path, anchor: str, destination: Path, destination_header: str) -> None:
    text = path.read_text(encoding="utf-8")
    if destination.exists():
        raise RuntimeError(f"Destination already exists: {destination}")
    index = text.find(anchor)
    if index < 0:
        raise RuntimeError(f"Anchor not found in {path}: {anchor!r}")
    prefix = text[:index]
    tail = text[index:]
    if not tail.rstrip().endswith("}"):
        raise RuntimeError(f"Source tail does not contain the class closing brace: {path}")
    path.write_text(prefix + "}\n", encoding="utf-8")
    destination.write_text(destination_header + tail, encoding="utf-8")


file_mover = Path("listenarr.infrastructure/FileSystem/FileMover.DirectoryCopy.cs")
split_once(
    file_mover,
    "    private async Task<bool> SourceSnapshotStillMatchesAsync(\n",
    Path("listenarr.infrastructure/FileSystem/FileMover.DirectoryCopy.Validation.cs"),
    """/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
""",
)

pinned = Path("listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.cs")
pinned_text = pinned.read_text(encoding="utf-8")
operations_anchor = "    private static SafeFileHandle OpenVisibleDirectory(string path) =>\n"
interop_anchor = "    private readonly record struct WindowsFileIdentity(\n"
operations_index = pinned_text.find(operations_anchor)
interop_index = pinned_text.find(interop_anchor)
if operations_index < 0 or interop_index < 0 or interop_index <= operations_index:
    raise RuntimeError("PinnedDirectoryCreation split anchors were not found in order")

main_text = pinned_text[:operations_index] + "}\n"
operations_text = pinned_text[operations_index:interop_index] + "}\n"
interop_text = pinned_text[interop_index:]
if not interop_text.rstrip().endswith("}"):
    raise RuntimeError("PinnedDirectoryCreation interop tail lacks the class closing brace")

pinned_operations = Path(
    "listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.NativeOperations.cs")
pinned_interop = Path(
    "listenarr.infrastructure/FileSystem/PinnedDirectoryCreation.NativeInterop.cs")
if pinned_operations.exists() or pinned_interop.exists():
    raise RuntimeError("PinnedDirectoryCreation split destination already exists")

pinned.write_text(main_text, encoding="utf-8")
pinned_operations.write_text(
    """using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
""" + operations_text,
    encoding="utf-8",
)
pinned_interop.write_text(
    """using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
""" + interop_text,
    encoding="utf-8",
)
