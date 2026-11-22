// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceAutomationLibrary.h"

#include "Framework/Application/SlateApplication.h"
#include "MantlePlaceCuratorExtentLogic.h"
#include "MantlePlaceExtentBox.h"
#include "MantlePlaceLevelExtent.h"
#include "Layout/WidgetPath.h"
#include "Types/ISlateMetaData.h"
#include "Widgets/SWidget.h"
#include "Widgets/SWindow.h"
#include "Widgets/Text/STextBlock.h"

namespace
{
	FName WidgetTag(const TSharedRef<SWidget>& Widget)
	{
		const FName Tag = Widget->GetTag();
		if (!Tag.IsNone())
		{
			return Tag;
		}
		const TSharedPtr<FTagMetaData> Meta = Widget->GetMetaData<FTagMetaData>();
		return Meta.IsValid() ? Meta->Tag : NAME_None;
	}

	void CollectTagged(const TSharedRef<SWidget>& Widget, TArray<TSharedRef<SWidget>>& Out)
	{
		if (!WidgetTag(Widget).IsNone())
		{
			Out.Add(Widget);
		}
		FChildren* Children = Widget->GetChildren();
		if (Children == nullptr)
		{
			return;
		}
		for (int32 Index = 0; Index < Children->Num(); ++Index)
		{
			CollectTagged(Children->GetChildAt(Index), Out);
		}
	}

	bool ReachableGeometry(const TSharedRef<SWidget>& Widget, FGeometry& OutGeometry)
	{
		FWidgetPath Path;
		if (!FSlateApplication::Get().FindPathToWidget(Widget, Path, EVisibility::Visible))
		{
			return false;
		}
		OutGeometry = Path.Widgets.Last().Geometry;
		return true;
	}

	void ForEachReachableTagged(
		const TFunctionRef<void(const FName&, const FGeometry&)> Visit)
	{
		if (!FSlateApplication::IsInitialized())
		{
			return;
		}
		TArray<TSharedRef<SWindow>> Windows;
		FSlateApplication::Get().GetAllVisibleWindowsOrdered(Windows);
		for (const TSharedRef<SWindow>& Window : Windows)
		{
			TArray<TSharedRef<SWidget>> Tagged;
			CollectTagged(Window, Tagged);
			for (const TSharedRef<SWidget>& Widget : Tagged)
			{
				FGeometry Geometry;
				if (ReachableGeometry(Widget, Geometry))
				{
					Visit(WidgetTag(Widget), Geometry);
				}
			}
		}
	}
}

bool UMantlePlaceAutomationLibrary::GetTaggedWidgetScreenRect(
	FName Tag, FVector2D& OutCenter, FVector2D& OutSize)
{
	bool bFound = false;
	ForEachReachableTagged([&](const FName& WidgetTag, const FGeometry& Geometry) {
		if (bFound || WidgetTag != Tag)
		{
			return;
		}
		const FVector2D TopLeft = FVector2D(Geometry.GetAbsolutePosition());
		const FVector2D Size = FVector2D(Geometry.GetAbsoluteSize());
		if (Size.X <= 0.0 || Size.Y <= 0.0)
		{
			return;
		}
		OutCenter = TopLeft + Size * 0.5;
		OutSize = Size;
		bFound = true;
	});
	return bFound;
}

namespace
{
	TSharedPtr<STextBlock> FindTextBlock(const TSharedRef<SWidget>& Widget)
	{
		if (Widget->GetType() == TEXT("STextBlock"))
		{
			return StaticCastSharedRef<STextBlock>(Widget);
		}
		FChildren* Children = Widget->GetChildren();
		if (Children != nullptr)
		{
			for (int32 Index = 0; Index < Children->Num(); ++Index)
			{
				if (TSharedPtr<STextBlock> Found = FindTextBlock(Children->GetChildAt(Index)))
				{
					return Found;
				}
			}
		}
		return nullptr;
	}
}

bool UMantlePlaceAutomationLibrary::GetTaggedWidgetText(FName Tag, FString& OutText)
{
	if (!FSlateApplication::IsInitialized())
	{
		return false;
	}
	TArray<TSharedRef<SWindow>> Windows;
	FSlateApplication::Get().GetAllVisibleWindowsOrdered(Windows);
	for (const TSharedRef<SWindow>& Window : Windows)
	{
		TArray<TSharedRef<SWidget>> Tagged;
		CollectTagged(Window, Tagged);
		for (const TSharedRef<SWidget>& Widget : Tagged)
		{
			FGeometry Geometry;
			if (WidgetTag(Widget) != Tag || !ReachableGeometry(Widget, Geometry))
			{
				continue;
			}
			if (const TSharedPtr<STextBlock> Text = FindTextBlock(Widget))
			{
				OutText = Text->GetText().ToString();
				return true;
			}
		}
	}
	return false;
}

TArray<FName> UMantlePlaceAutomationLibrary::ListReachableWidgetTags()
{
	TArray<FName> Tags;
	ForEachReachableTagged([&](const FName& WidgetTag, const FGeometry& Geometry) {
		if (WidgetTag.ToString().StartsWith(TEXT("MantlePlace."))
			&& Geometry.GetAbsoluteSize().X > 0.0f)
		{
			Tags.AddUnique(WidgetTag);
		}
	});
	return Tags;
}

namespace
{
	EMantlePlaceCuratorSource ToCuratorSource(EMantlePlaceExtentSource Source)
	{
		switch (Source)
		{
		case EMantlePlaceExtentSource::PriorImport:
			return EMantlePlaceCuratorSource::PriorImport;
		case EMantlePlaceExtentSource::NativeGeoreference:
			return EMantlePlaceCuratorSource::NativeGeoreference;
		case EMantlePlaceExtentSource::CesiumGeoreference:
			return EMantlePlaceCuratorSource::CesiumGeoreference;
		case EMantlePlaceExtentSource::ExtentBox:
			return EMantlePlaceCuratorSource::ExtentBox;
		case EMantlePlaceExtentSource::None:
			break;
		}
		return EMantlePlaceCuratorSource::None;
	}
}

FMantlePlaceCuratorResolution UMantlePlaceAutomationLibrary::ResolveCuratorExtent()
{
	return ResolveCuratorExtentInWorld(MantlePlaceLevelExtent::EditorWorld());
}

FMantlePlaceCuratorResolution UMantlePlaceAutomationLibrary::ResolveCuratorExtentInWorld(UWorld* World)
{
	const FMantlePlaceResolvedExtent Resolved = MantlePlaceLevelExtent::Resolve(World);

	FMantlePlaceCuratorResolution Result;
	Result.Source = ToCuratorSource(Resolved.Source);
	Result.SourceDescription = FMantlePlaceCuratorExtentLogic::DescribeSource(Resolved.Source);
	Result.bHasExtent = Resolved.IsSet();
	if (Resolved.IsSet())
	{
		Result.West = Resolved.Extent.West;
		Result.South = Resolved.Extent.South;
		Result.East = Resolved.Extent.East;
		Result.North = Resolved.Extent.North;
	}
	Result.Url = MantlePlaceLevelExtent::CuratorUrl(Resolved);
	return Result;
}

AActor* UMantlePlaceAutomationLibrary::PlaceExtentBox()
{
	return PlaceExtentBoxInWorld(MantlePlaceLevelExtent::EditorWorld());
}

AActor* UMantlePlaceAutomationLibrary::PlaceExtentBoxInWorld(UWorld* World)
{
	return MantlePlaceLevelExtent::PlaceExtentBox(World);
}
