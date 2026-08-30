// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceAutomationLibrary.h"

#include "Framework/Application/SlateApplication.h"
#include "Layout/WidgetPath.h"
#include "Types/ISlateMetaData.h"
#include "Widgets/SWidget.h"
#include "Widgets/SWindow.h"

namespace
{
	/**
	 * Depth-first sweep for tagged widgets. Cheap: runs on demand, not per
	 * frame. Both tagging surfaces are honoured — the declarative `.Tag()`
	 * argument sets the SWidget::Tag NAME (not metadata), while imperative
	 * code may attach FTagMetaData — because a caller cannot tell from the
	 * outside which one a widget author used.
	 */
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

	/**
	 * The widget's current arranged geometry, or unset when it is not
	 * reachable in the live layout. FindPathToWidget arranges from the window
	 * down, so a widget parked on an SWidgetSwitcher's dormant page — whose
	 * cached paint geometry is stale but nonzero — correctly fails here.
	 */
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
} // namespace

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
