// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

class UObject;

namespace MantlePlaceImportNaming
{
	/**
	 * Rename a freshly-imported asset so it satisfies the project's asset-naming standard
	 * (NativeStyleGuide §8: SM_/T_/MI_/M_ prefixes) — imported vault content lands under the
	 * policed /Game/MantlePlace/ namespace. No-op for classes without a mandated prefix or
	 * names that are already compliant. References are fixed up by the rename.
	 */
	void RenameToConvention(UObject* Asset);
}
