// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlacePublicRecordsLogic.h"

#include "MantlePlaceImportManifest.h"

namespace
{
const TCHAR* const KnownFamilies[] = {
	TEXT("parcels"), TEXT("zoning"), TEXT("flood"), TEXT("wetlands"), TEXT("soils"),
	TEXT("protected"), TEXT("hydrography"), TEXT("habitat"), TEXT("contamination"), TEXT("historic"),
};

const TCHAR* const KnownDisplayNames[] = {
	TEXT("Parcels"), TEXT("Zoning"), TEXT("Flood zones"), TEXT("Wetlands"), TEXT("Soils"),
	TEXT("Protected areas"), TEXT("Hydrography"), TEXT("Habitat"), TEXT("Contamination"), TEXT("Historic"),
};

static_assert(UE_ARRAY_COUNT(KnownFamilies) == UE_ARRAY_COUNT(KnownDisplayNames), "one display name per known family");

struct FFamilyAlias
{
	const TCHAR* Name;
	const TCHAR* Family;
};
const FFamilyAlias FamilyAliases[] = {
	{ TEXT("floodplain"), TEXT("flood") },
	{ TEXT("conservation"), TEXT("protected") },
	{ TEXT("heritage"), TEXT("historic") },
};

int32 KnownIndex(const FString& Family)
{
	for (const FFamilyAlias& Alias : FamilyAliases)
	{
		if (Family.Equals(Alias.Name, ESearchCase::CaseSensitive))
		{
			return KnownIndex(FString(Alias.Family));
		}
	}
	for (int32 Index = 0; Index < UE_ARRAY_COUNT(KnownFamilies); ++Index)
	{
		if (Family.Equals(KnownFamilies[Index], ESearchCase::CaseSensitive))
		{
			return Index;
		}
	}
	return INDEX_NONE;
}

FString Shown(const FString& Value)
{
	return Value.IsEmpty() ? FString(TEXT("(not stated)")) : FString::Printf(TEXT("\"%s\""), *Value);
}

struct FDxfPair
{
	int32 Code = 0;
	FString Value;
};

struct FDxfEntity
{
	FString Type;
	int32 Flags = 0;
	TArray<FVector2D> Points;
	double EndX = 0.0;
	double EndY = 0.0;
	bool bHasEnd = false;
	TArray<FDxfPair> Raw;
};

int32 FindCode(const TArray<FDxfPair>& Pairs, int32 From, int32 Code)
{
	for (int32 Index = From; Index < Pairs.Num(); ++Index)
	{
		if (Pairs[Index].Code == Code)
		{
			return Index;
		}
	}
	return INDEX_NONE;
}

bool ReadHatchBoundaries(const TArray<FDxfPair>& Raw, TArray<FMantlePlaceRecordPolyline>& OutRings)
{
	OutRings.Reset();
	int32 At = FindCode(Raw, 0, 91);
	if (At == INDEX_NONE)
	{
		return false;
	}
	const int32 PathCount = FCString::Atoi(*Raw[At].Value);
	++At;
	for (int32 Path = 0; Path < PathCount; ++Path)
	{
		At = FindCode(Raw, At, 92);
		if (At == INDEX_NONE)
		{
			return false;
		}
		const int32 Flags = FCString::Atoi(*Raw[At].Value);
		++At;
		FMantlePlaceRecordPolyline Ring;
		Ring.bClosed = true;
		At = FindCode(Raw, At, 93);
		if (At == INDEX_NONE)
		{
			return false;
		}
		const int32 Count = FCString::Atoi(*Raw[At].Value);
		++At;
		if ((Flags & 2) != 0)
		{
			int32 YsRead = 0;
			for (; At < Raw.Num() && YsRead < Count; ++At)
			{
				if (Raw[At].Code == 10)
				{
					Ring.Points.Add(FVector2D(FCString::Atod(*Raw[At].Value), 0.0));
				}
				else if (Raw[At].Code == 20 && Ring.Points.Num() > 0)
				{
					Ring.Points.Last().Y = FCString::Atod(*Raw[At].Value);
					++YsRead;
				}
			}
			if (YsRead < Count)
			{
				return false;
			}
		}
		else
		{
			FVector2D LastEnd = FVector2D::ZeroVector;
			for (int32 Edge = 0; Edge < Count; ++Edge)
			{
				if (At >= Raw.Num() || Raw[At].Code != 72 || FCString::Atoi(*Raw[At].Value) != 1)
				{
					return false;
				}
				++At;
				FVector2D Start = FVector2D::ZeroVector;
				FVector2D End = FVector2D::ZeroVector;
				int32 Have = 0;
				for (; At < Raw.Num() && Have != 15; ++At)
				{
					switch (Raw[At].Code)
					{
					case 10: Start.X = FCString::Atod(*Raw[At].Value); Have |= 1; break;
					case 20: Start.Y = FCString::Atod(*Raw[At].Value); Have |= 2; break;
					case 11: End.X = FCString::Atod(*Raw[At].Value); Have |= 4; break;
					case 21: End.Y = FCString::Atod(*Raw[At].Value); Have |= 8; break;
					default: break;
					}
				}
				if (Have != 15)
				{
					return false;
				}
				Ring.Points.Add(Start);
				LastEnd = End;
			}
			if (Ring.Points.Num() > 0 && !LastEnd.Equals(Ring.Points[0], 1e-9))
			{
				Ring.Points.Add(LastEnd);
			}
		}
		if (Ring.Points.Num() < 3)
		{
			return false;
		}
		OutRings.Add(MoveTemp(Ring));
	}
	return OutRings.Num() > 0;
}
}

bool FMantlePlacePublicRecordsLogic::IsKnownFamily(const FString& Family)
{
	return KnownIndex(Family) != INDEX_NONE;
}

FString FMantlePlacePublicRecordsLogic::DisplayName(const FString& Family)
{
	const int32 Known = KnownIndex(Family);
	if (Known != INDEX_NONE)
	{
		return KnownDisplayNames[Known];
	}
	FString Name;
	bool bWordStart = true;
	for (const TCHAR Ch : Family)
	{
		if (Ch == TEXT('_') || Ch == TEXT('-') || Ch == TEXT(' '))
		{
			if (!Name.IsEmpty() && !Name.EndsWith(TEXT(" ")))
			{
				Name.AppendChar(TEXT(' '));
			}
			bWordStart = true;
			continue;
		}
		Name.AppendChar(bWordStart ? FChar::ToUpper(Ch) : Ch);
		bWordStart = false;
	}
	Name.TrimEndInline();
	return Name.IsEmpty() ? Family : Name;
}

void FMantlePlacePublicRecordsLogic::OrderLayers(TArray<FMantlePlaceRecordLayer>& Layers)
{
	Layers.StableSort([](const FMantlePlaceRecordLayer& A, const FMantlePlaceRecordLayer& B)
	{
		const int32 IndexA = KnownIndex(A.Family);
		const int32 IndexB = KnownIndex(B.Family);
		const bool bKnownA = IndexA != INDEX_NONE;
		const bool bKnownB = IndexB != INDEX_NONE;
		if (bKnownA != bKnownB)
		{
			return bKnownA;
		}
		if (bKnownA)
		{
			return IndexA < IndexB;
		}
		return A.Family < B.Family;
	});
}

FString FMantlePlacePublicRecordsLogic::FrameTypeOfCoordinateMode(const FString& CoordinateMode)
{
	if (CoordinateMode == TEXT("absolute"))
	{
		return TEXT("projected");
	}
	if (CoordinateMode == TEXT("offset"))
	{
		return TEXT("local");
	}
	return CoordinateMode;
}

FMantlePlaceRecordFrame FMantlePlacePublicRecordsLogic::StatedFrame(
    const FString& Type, const FString& Crs, bool bHasUnitFactor, double UnitToMetres, const FString& UnitName)
{
	FMantlePlaceRecordFrame Frame;
	Frame.bStated = true;
	Frame.Type = Type;
	Frame.Crs = Crs;
	if (bHasUnitFactor)
	{
		Frame.HorizontalUnit = UnitToMetres == 1.0
		                           ? FString(TEXT("m"))
		                           : (UnitName.IsEmpty() ? FString::Printf(TEXT("%.10g m per unit"), UnitToMetres)
		                                                 : FString::Printf(TEXT("%s (%.10g m)"), *UnitName, UnitToMetres));
	}
	return Frame;
}

bool FMantlePlacePublicRecordsLogic::CanPlaceFrame(
    const FMantlePlaceRecordFrame& Frame, const FString& HostCrs, FString& OutReason)
{
	if (!Frame.bStated)
	{
		OutReason = TEXT("the manifest states no frame for the file, and an unstated frame is never assumed to match this host's");
		return false;
	}
	if (!Frame.Type.Equals(TEXT("projected"), ESearchCase::CaseSensitive))
	{
		OutReason = FString::Printf(
		    TEXT("the file's frame is of type %s, not a projected frame in this host's grid"), *Shown(Frame.Type));
		return false;
	}
	if (Frame.Crs.IsEmpty() || Frame.HorizontalUnit.IsEmpty())
	{
		OutReason = FString::Printf(
		    TEXT("the file states its frame only in part (CRS %s, horizontal unit %s)"),
		    *Shown(Frame.Crs), *Shown(Frame.HorizontalUnit));
		return false;
	}
	if (HostCrs.IsEmpty() || !Frame.Crs.Equals(HostCrs, ESearchCase::CaseSensitive))
	{
		OutReason = FString::Printf(
		    TEXT("the file is in %s, and this host places in %s"), *Shown(Frame.Crs), *Shown(HostCrs));
		return false;
	}
	if (!Frame.HorizontalUnit.Equals(TEXT("m"), ESearchCase::CaseSensitive))
	{
		OutReason = FString::Printf(
		    TEXT("the file's horizontal unit is %s, and this host places in metres (nothing is scaled)"),
		    *Shown(Frame.HorizontalUnit));
		return false;
	}
	OutReason.Reset();
	return true;
}

bool FMantlePlacePublicRecordsLogic::ParseDxf(
    const FString& Text,
    TArray<FMantlePlaceRecordPolyline>& OutLines,
    TArray<FVector2D>& OutPoints,
    int32& OutEntitiesSkipped,
    FString& OutError)
{
	OutLines.Reset();
	OutPoints.Reset();
	OutEntitiesSkipped = 0;
	OutError.Reset();

	if (Text.Contains(TEXT("AutoCAD Binary DXF")))
	{
		OutError = TEXT("the DXF is the binary form, which this reader does not read (it reads ASCII DXF)");
		return false;
	}

	TArray<FString> Lines;
	Text.ParseIntoArrayLines(Lines, false);
	TArray<FDxfPair> Pairs;
	Pairs.Reserve(Lines.Num() / 2);
	for (int32 Index = 0; Index + 1 < Lines.Num(); Index += 2)
	{
		const FString Code = Lines[Index].TrimStartAndEnd();
		if (Code.IsEmpty() || !Code.IsNumeric())
		{
			OutError = FString::Printf(TEXT("the file is not a DXF drawing (line %d is not a group code)"), Index + 1);
			return false;
		}
		Pairs.Add(FDxfPair{ FCString::Atoi(*Code), Lines[Index + 1].TrimStartAndEnd() });
	}

	FString Section;
	bool bExpectSectionName = false;
	bool bSawEntities = false;
	FDxfEntity Entity;
	bool bInPolyline = false;
	bool bPolylineSkipped = false;
	FMantlePlaceRecordPolyline Poly;

	auto Keep = [&OutLines](FMantlePlaceRecordPolyline Line)
	{
		OutLines.Add(MoveTemp(Line));
	};

	auto Finish = [&]()
	{
		if (Entity.Type.IsEmpty())
		{
			return;
		}
		if (Entity.Type == TEXT("LWPOLYLINE"))
		{
			if (Entity.Points.Num() >= 2)
			{
				FMantlePlaceRecordPolyline Line;
				Line.Points = Entity.Points;
				Line.bClosed = (Entity.Flags & 1) != 0;
				Keep(MoveTemp(Line));
			}
			else
			{
				++OutEntitiesSkipped;
			}
		}
		else if (Entity.Type == TEXT("POLYLINE"))
		{
			bInPolyline = true;
			bPolylineSkipped = (Entity.Flags & (16 | 64)) != 0;
			Poly = FMantlePlaceRecordPolyline();
			Poly.bClosed = (Entity.Flags & 1) != 0;
		}
		else if (Entity.Type == TEXT("VERTEX"))
		{
			if (bInPolyline && !bPolylineSkipped && (Entity.Flags & 16) == 0 && Entity.Points.Num() > 0)
			{
				Poly.Points.Add(Entity.Points[0]);
			}
		}
		else if (Entity.Type == TEXT("SEQEND"))
		{
			if (bInPolyline)
			{
				if (!bPolylineSkipped && Poly.Points.Num() >= 2)
				{
					Keep(Poly);
				}
				else
				{
					++OutEntitiesSkipped;
				}
			}
			bInPolyline = false;
			Poly = FMantlePlaceRecordPolyline();
		}
		else if (Entity.Type == TEXT("LINE"))
		{
			if (Entity.Points.Num() > 0 && Entity.bHasEnd)
			{
				FMantlePlaceRecordPolyline Line;
				Line.Points.Add(Entity.Points[0]);
				Line.Points.Add(FVector2D(Entity.EndX, Entity.EndY));
				Keep(MoveTemp(Line));
			}
			else
			{
				++OutEntitiesSkipped;
			}
		}
		else if (Entity.Type == TEXT("HATCH"))
		{
			TArray<FMantlePlaceRecordPolyline> Rings;
			if (ReadHatchBoundaries(Entity.Raw, Rings))
			{
				for (FMantlePlaceRecordPolyline& Ring : Rings)
				{
					Keep(MoveTemp(Ring));
				}
			}
			else
			{
				++OutEntitiesSkipped;
			}
		}
		else if (Entity.Type == TEXT("POINT"))
		{
			if (Entity.Points.Num() > 0)
			{
				OutPoints.Add(Entity.Points[0]);
			}
			else
			{
				++OutEntitiesSkipped;
			}
		}
		else
		{
			++OutEntitiesSkipped;
		}
		Entity = FDxfEntity();
	};

	for (const FDxfPair& Pair : Pairs)
	{
		if (Pair.Code == 0)
		{
			Finish();
			if (Pair.Value == TEXT("SECTION"))
			{
				bExpectSectionName = true;
			}
			else if (Pair.Value == TEXT("ENDSEC") || Pair.Value == TEXT("EOF"))
			{
				Section.Reset();
			}
			else if (Section == TEXT("ENTITIES"))
			{
				Entity.Type = Pair.Value;
			}
			continue;
		}
		if (bExpectSectionName && Pair.Code == 2)
		{
			Section = Pair.Value;
			bExpectSectionName = false;
			bSawEntities = bSawEntities || Section == TEXT("ENTITIES");
			continue;
		}
		if (Entity.Type.IsEmpty())
		{
			continue;
		}
		if (Entity.Type == TEXT("HATCH"))
		{
			Entity.Raw.Add(Pair);
		}
		switch (Pair.Code)
		{
		case 70:
			Entity.Flags = FCString::Atoi(*Pair.Value);
			break;
		case 10:
			Entity.Points.Add(FVector2D(FCString::Atod(*Pair.Value), 0.0));
			break;
		case 20:
			if (Entity.Points.Num() > 0)
			{
				Entity.Points.Last().Y = FCString::Atod(*Pair.Value);
			}
			break;
		case 11:
			Entity.EndX = FCString::Atod(*Pair.Value);
			Entity.bHasEnd = true;
			break;
		case 21:
			Entity.EndY = FCString::Atod(*Pair.Value);
			break;
		default:
			break;
		}
	}
	Finish();

	if (!bSawEntities)
	{
		OutError = TEXT("the file is not a DXF drawing (it has no ENTITIES section)");
		OutLines.Reset();
		return false;
	}
	return true;
}

FMantlePlaceRecordPolyline FMantlePlacePublicRecordsLogic::MarkerOutline(const FVector2D& Point)
{
	FMantlePlaceRecordPolyline Marker;
	Marker.bClosed = true;
	Marker.Points.Add(FVector2D(Point.X + MarkerRadiusM, Point.Y));
	Marker.Points.Add(FVector2D(Point.X, Point.Y + MarkerRadiusM));
	Marker.Points.Add(FVector2D(Point.X - MarkerRadiusM, Point.Y));
	Marker.Points.Add(FVector2D(Point.X, Point.Y - MarkerRadiusM));
	return Marker;
}

bool FMantlePlacePublicRecordsLogic::AnyVertexOnSite(
    const TArray<FMantlePlaceRecordPolyline>& Lines,
    double OriginEastingM,
    double OriginNorthingM,
    const FVector2D& SpanUeCm)
{
	if (SpanUeCm.X <= 0.0 || SpanUeCm.Y <= 0.0)
	{
		return true;
	}
	for (const FMantlePlaceRecordPolyline& Line : Lines)
	{
		for (const FVector2D& Point : Line.Points)
		{
			const FVector Ue = FMantlePlaceVaultManifest::ProjectedToUeCm(
			    Point.X - OriginEastingM, Point.Y - OriginNorthingM, 0.0);
			if (FMath::Abs(Ue.X) <= SpanUeCm.X * 0.5 && FMath::Abs(Ue.Y) <= SpanUeCm.Y * 0.5)
			{
				return true;
			}
		}
	}
	return Lines.Num() == 0;
}

FString FMantlePlacePublicRecordsLogic::StandingNotice()
{
	return TEXT(
	    "Public-record layers are the record holders' data, passed through unchanged. They are not a "
	    "survey and not a legal determination: verify against the record holder before relying on them.");
}

TArray<FString> FMantlePlacePublicRecordsLogic::DescribeLayer(const FMantlePlaceRecordLayer& Layer)
{
	const FString Name = DisplayName(Layer.Family);
	FString Credit = Layer.Credit;
	if (Credit.IsEmpty())
	{
		for (const FMantlePlaceRecordFile& File : Layer.Files)
		{
			if (!File.Source.IsEmpty())
			{
				Credit = File.Source;
				if (!File.License.IsEmpty())
				{
					Credit += FString::Printf(TEXT(" (licence: %s)"), *File.License);
				}
				break;
			}
		}
	}

	TArray<FString> Out;
	Out.Add(Credit.IsEmpty()
	            ? FString::Printf(TEXT("%s: the manifest names no credit line for this layer."), *Name)
	            : FString::Printf(TEXT("%s: credit %s."), *Name, *Credit));
	if (!Layer.Disclaimer.IsEmpty())
	{
		Out.Add(FString::Printf(TEXT("%s disclaimer: %s"), *Name, *Layer.Disclaimer));
	}
	if (!Layer.Notice.IsEmpty())
	{
		Out.Add(FString::Printf(TEXT("%s notice: %s"), *Name, *Layer.Notice));
	}
	return Out;
}

FString FMantlePlacePublicRecordsLogic::DescribePointer(const FString& Family, const FMantlePlaceRecordPointer& Pointer)
{
	const FString Name = Family.IsEmpty() ? FString(TEXT("Record")) : DisplayName(Family);
	FString Why;
	if (Pointer.Reason == TEXT("terms_restrict_commercial_use"))
	{
		Why = Pointer.Basis.IsEmpty()
		          ? FString(TEXT("its terms restrict commercial use"))
		          : FString::Printf(TEXT("its terms restrict commercial use (%s)"), *Pointer.Basis);
	}
	else if (Pointer.Reason == TEXT("down_at_build_time"))
	{
		Why = TEXT("its source was down when the bundle was built");
	}
	else
	{
		Why = Pointer.Basis.IsEmpty()
		          ? FString(TEXT("no open source covers it"))
		          : FString::Printf(TEXT("no open source covers it (%s)"), *Pointer.Basis);
	}
	const FString Where = Pointer.Link.IsEmpty()
	                          ? FString(TEXT("The manifest gives no page for it."))
	                          : FString::Printf(TEXT("The record holder publishes it at %s."), *Pointer.Link);
	const FString& Area = Pointer.Jurisdiction.IsEmpty() ? Pointer.RecordHolder : Pointer.Jurisdiction;
	return FString::Printf(
	    TEXT("%s not delivered for %s: the record %s holds is not in this bundle, because %s. %s"),
	    *Name, *Area, *Pointer.RecordHolder, *Why, *Where);
}

FString FMantlePlacePublicRecordsLogic::DescribeSkippedSource(const FMantlePlaceSkippedSource& Source)
{
	const FString Name = Source.Layer.IsEmpty() ? FString(TEXT("A record")) : DisplayName(Source.Layer);
	FString Why;
	if (Source.Reason == TEXT("source_down"))
	{
		Why = TEXT("the source was marked unavailable when the bundle was built");
	}
	else if (Source.Reason == TEXT("source_timeout"))
	{
		Why = TEXT("the source ran out of time");
	}
	else
	{
		Why = TEXT("the source did not answer");
	}
	return FString::Printf(
	    TEXT("%s from %s is missing from this bundle: %s (source %s)."),
	    *Name, *Source.RecordHolder, *Why, *Source.SourceId);
}

TArray<FString> FMantlePlacePublicRecordsLogic::DescribeNotDelivered(const FMantlePlacePublicRecordsBlock& Block)
{
	TArray<FString> Out;
	for (const FMantlePlaceRecordLayer& Layer : Block.Layers)
	{
		for (const FMantlePlaceRecordPointer& Pointer : Layer.Pointers)
		{
			Out.Add(DescribePointer(Layer.Family, Pointer));
		}
	}
	for (const FMantlePlaceSkippedSource& Source : Block.SkippedSources)
	{
		Out.Add(DescribeSkippedSource(Source));
	}
	TArray<FString> Seen;
	for (const FMantlePlaceRecordNotHeld& First : Block.NotHeld)
	{
		const FString Key = First.Family + TEXT("|") + First.Reason;
		if (Seen.Contains(Key))
		{
			continue;
		}
		Seen.Add(Key);
		TArray<FString> Paths;
		for (const FMantlePlaceRecordNotHeld& Other : Block.NotHeld)
		{
			if (Other.Family == First.Family && Other.Reason == First.Reason)
			{
				Paths.Add(Other.Path);
			}
		}
		Out.Add(FString::Printf(
		    TEXT("%s not in this download (%s)%s"), *DisplayName(First.Family), *FString::Join(Paths, TEXT(", ")),
		    First.Reason.IsEmpty() ? TEXT(".") : *FString::Printf(TEXT(": %s"), *First.Reason)));
	}
	return Out;
}
