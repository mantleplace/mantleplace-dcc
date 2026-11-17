// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceBundleReaderLogic.h"

/**
 * Opens a bundle on disk for the reader: a folder, or a zip, either of which may hold the bundle inside
 * one wrapper folder. This unit only lists the entries and reads one as text; every decision about what
 * they mean is FMantlePlaceBundleReaderLogic's. A zip is read through the engine's FZipArchiveReader,
 * the reader the import uses, so an entry name matches regardless of case here as it does there.
 */
namespace MantlePlaceBundleArchive
{
/** Read the bundle at `Path`. A path that is neither a folder nor a readable zip is a failed read. */
FMantlePlaceBundleRead Read(const FString& Path);
}
