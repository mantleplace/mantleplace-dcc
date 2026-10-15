// Copyright Mantle Place. All Rights Reserved.

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

/** The position in the published order, or INDEX_NONE for a family this reader has not met. */
int32 KnownIndex(const FString& Family)
{
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

/** One DXF group-code/value pair. */
struct FDxfPair
{
	int32 Code = 0;
	FString Value;
};

/** What one entity has accumulated between its `0` record and the next. */
struct FDxfEntity
{
	FString Type;
	int32 Flags = 0;
	TArray<FVector2D> Points; // LWPOLYLINE: every 10/20 pair; VERTEX: the one; LINE: the start
	double EndX = 0.0;
	double EndY = 0.0;
	bool bHasEnd = false;
};
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
			return bKnownA; // the families the layout names come first
		}
		if (bKnownA)
		{
			return IndexA < IndexB;
		}
		return A.Family < B.Family;
	});
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
    int32& OutEntitiesSkipped,
    FString& OutError)
{
	OutLines.Reset();
	OutEntitiesSkipped = 0;
	OutError.Reset();

	if (Text.Contains(TEXT("AutoCAD Binary DXF")))
	{
		OutError = TEXT("the DXF is the binary form, which this reader does not read (it reads ASCII DXF)");
		return false;
	}

	TArray<FString> Lines;
	Text.ParseIntoArrayLines(Lines, /*bCullEmpty*/ false);
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
	bool bInPolyline = false;      // between a POLYLINE record and its SEQEND
	bool bPolylineSkipped = false; // a polyface or 3D mesh: its vertices are not a line
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
			bPolylineSkipped = (Entity.Flags & (16 | 64)) != 0; // 3D mesh (16), polyface mesh (64)
			Poly = FMantlePlaceRecordPolyline();
			Poly.bClosed = (Entity.Flags & 1) != 0;
		}
		else if (Entity.Type == TEXT("VERTEX"))
		{
			// 16 is a spline frame control point: not on the curve, so not a vertex of the line.
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
		switch (Pair.Code)
		{
		case 70:
			Entity.Flags = FCString::Atoi(*Pair.Value);
			break;
		case 10:
			// LWPOLYLINE repeats the pair per vertex; VERTEX and LINE carry one. A POLYLINE's own 10/20/30
			// is a dummy elevation record, which Finish does not read for that type. An arc segment's
			// bulge (code 42) is not read: the vertices are joined straight, never re-derived.
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

bool FMantlePlacePublicRecordsLogic::AnyVertexOnSite(
    const TArray<FMantlePlaceRecordPolyline>& Lines,
    double OriginEastingM,
    double OriginNorthingM,
    const FVector2D& SpanUeCm)
{
	if (SpanUeCm.X <= 0.0 || SpanUeCm.Y <= 0.0)
	{
		return true; // no published span: no evidence either way
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
		// No credit line of the layer's own: the source its file entries name is the credit.
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
		// no_open_source, and a reason this reader does not know: the record is not in the bundle either way.
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
		// source_failed, and a reason this reader does not know.
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
	return Out;
}
