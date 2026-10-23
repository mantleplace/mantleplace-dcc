# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Read a bundle: every file with its frame and role, and how a file sits inside its own frame.

    from mantleplace_mpb import read_bundle
    with read_bundle("site.zip") as bundle:
        for f in bundle.files:
            print(f.path, f.role, f.frame and f.frame.coordinate_mode)
        move = bundle.transform("Site/Terrain.glb", "Elevation/DEM.tif")
        move.apply((10.0, 5.0, 20.0))

The reader states what the manifest states and does the arithmetic the spec allows (format.md
6.8): an axis transform from a file's `axis_mapping`, a unit factor, and an offset from the site
origin, added. **It never reprojects.** Two files whose horizontal CRS, vertical CRS, geoid or
epoch differ are in different frames, and asking for one in terms of the other raises
`FrameMismatch`. A CRS is compared as written (a `code` case-insensitively, a PROJJSON object or
a WKT2 string exactly), so two unlike spellings of one CRS are different frames here, never
resolved. There is no schema fetch and no network: `validate` holds a bundle to the schema; this
reads one.

A frame the reader cannot read (a value outside the known sets, a field missing, two axes on one
line) leaves the file in the listing with `frame` None and `frame_problem` saying why: it is a
file the reader cannot place, refused by name, never guessed at. A missing or unknown ODbL role
reads as `database`, the stricter duty (format.md 6.8).

Linear units are taken as the manifest states them (`unit_to_metres`); the reader does not know
which CRS is geographic.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import IO, Iterator

from .archive import MANIFEST_PATH, open_bundle
from .report import Report

Vector = tuple[float, float, float]
Matrix = tuple[Vector, Vector, Vector]

#: The unit of a site origin's projected point, as the schema names them, as a factor to metres.
_ORIGIN_UNITS = {"m": 1.0, "ft": 0.3048, "usft": 1200 / 3937}
#: Where each named direction points, as (east, north, up).
_DIRECTIONS: dict[str, Vector] = {
    "east": (1, 0, 0), "west": (-1, 0, 0), "north": (0, 1, 0), "south": (0, -1, 0),
    "up": (0, 0, 1), "down": (0, 0, -1)}
_AXIS_ORDERS = frozenset({"east-north", "north-east"})
_COORDINATE_MODES = frozenset({"absolute", "offset"})
_PIXEL_CONVENTIONS = frozenset({"pixel-is-area", "pixel-is-point"})
_ROW_ORDERS = frozenset({"north-up", "south-up"})
_ROLES = frozenset({"database", "produced-work", "none"})
_REQUIRED = ("horizontal_crs", "unit_to_metres", "axis_order", "axis_mapping", "coordinate_mode")
_IDENTITY: Matrix = ((1, 0, 0), (0, 1, 0), (0, 0, 1))


class BundleError(Exception):
    """The input is not a bundle this reader can open: unreadable, or no usable manifest."""


class FrameError(Exception):
    """A file cannot be placed from what the manifest states, short of reprojecting."""


class FrameMismatch(FrameError):
    """Two files are in different frames; relating them would take a reprojection."""


def _multiply(a: Matrix, b: Matrix) -> Matrix:
    rows = tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))
    return rows  # type: ignore[return-value]


def _transpose(a: Matrix) -> Matrix:
    return tuple(tuple(a[j][i] for j in range(3)) for i in range(3))  # type: ignore[return-value]


def _apply(a: Matrix, v: Vector) -> Vector:
    return tuple(sum(a[i][k] * v[k] for k in range(3)) for i in range(3))  # type: ignore[return-value]


def _crs_key(reference: object) -> tuple[str, str] | None:
    """A CRS reference reduced to a comparable key, or None when it is not one."""
    if not isinstance(reference, dict):
        return None
    if isinstance(reference.get("code"), str):
        return ("code", reference["code"].strip().upper())
    if isinstance(reference.get("projjson"), dict):
        return ("projjson", json.dumps(reference["projjson"], sort_keys=True))
    if isinstance(reference.get("wkt2"), str):
        return ("wkt2", " ".join(reference["wkt2"].split()))
    return None


def _number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


@dataclass(frozen=True)
class Frame:
    """A file's frame, written whole (format.md 6.8). Built only from a frame that can be read."""

    horizontal_crs: dict
    unit_to_metres: float
    axis_order: str
    axis_mapping: dict
    coordinate_mode: str
    vertical_crs: dict | None = None
    #: The vertical unit: `vertical_unit_to_metres` where stated, else the horizontal unit.
    vertical_unit_to_metres: float = 1.0
    geoid_model: str | None = None
    pixel_convention: str | None = None
    row_order: str | None = None
    coordinate_epoch: float | None = None

    @classmethod
    def from_entry(cls, raw: object) -> "Frame":
        """The frame a file entry states, or `FrameError` naming what makes it unreadable."""
        if not isinstance(raw, dict):
            raise FrameError("the frame is not an object")
        missing = [k for k in _REQUIRED if k not in raw]
        if missing:
            raise FrameError(f"the frame lacks {', '.join(missing)}; a frame is written whole")
        if _crs_key(raw["horizontal_crs"]) is None:
            raise FrameError("horizontal_crs is not a CRS reference (a code, PROJJSON or WKT2)")
        if raw.get("vertical_crs") is not None and _crs_key(raw["vertical_crs"]) is None:
            raise FrameError("vertical_crs is not a CRS reference (a code, PROJJSON or WKT2)")
        unit = raw["unit_to_metres"]
        vertical_unit = raw.get("vertical_unit_to_metres", unit)
        for name, value in (("unit_to_metres", unit), ("vertical_unit_to_metres", vertical_unit)):
            if not _number(value) or value <= 0:
                raise FrameError(f"{name} {value!r} is not a positive number")
        for name, known in (("axis_order", _AXIS_ORDERS), ("coordinate_mode", _COORDINATE_MODES),
                            ("pixel_convention", _PIXEL_CONVENTIONS), ("row_order", _ROW_ORDERS)):
            if name in raw and raw[name] not in known:
                raise FrameError(f"{name} {raw[name]!r} is not one of {', '.join(sorted(known))}")
        mapping = raw["axis_mapping"]
        if not isinstance(mapping, dict):
            raise FrameError("axis_mapping is not an object")
        for axis in ("x", "y", "z"):
            if mapping.get(axis) not in _DIRECTIONS:
                raise FrameError(f"axis_mapping.{axis} {mapping.get(axis)!r} is not one of "
                                 f"{', '.join(_DIRECTIONS)}")
        lines = {tuple(abs(c) for c in _DIRECTIONS[mapping[a]]) for a in ("x", "y", "z")}
        if len(lines) < 3:
            raise FrameError(f"axis_mapping puts two axes along one line "
                             f"({mapping['x']}, {mapping['y']}, {mapping['z']})")
        epoch = raw.get("coordinate_epoch")
        return cls(
            horizontal_crs=dict(raw["horizontal_crs"]), unit_to_metres=float(unit),
            axis_order=raw["axis_order"], axis_mapping={a: mapping[a] for a in ("x", "y", "z")},
            coordinate_mode=raw["coordinate_mode"],
            vertical_crs=dict(raw["vertical_crs"]) if raw.get("vertical_crs") else None,
            vertical_unit_to_metres=float(vertical_unit),
            geoid_model=raw.get("geoid_model"), pixel_convention=raw.get("pixel_convention"),
            row_order=raw.get("row_order"), coordinate_epoch=epoch if _number(epoch) else None)

    @property
    def axis_transform(self) -> Matrix:
        """Rows east, north, up; columns the file's x, y, z. `enu = axis_transform @ xyz`."""
        columns = [_DIRECTIONS[self.axis_mapping[a]] for a in ("x", "y", "z")]
        return tuple(tuple(columns[j][i] for j in range(3)) for i in range(3))  # type: ignore[return-value]

    def same_frame_as(self, other: "Frame") -> bool:
        """The same horizontal CRS, vertical CRS, geoid and epoch, compared as written.

        Axes and units are not part of a frame's identity: they are what `transform` resolves
        between two files of one frame.
        """
        return ((_crs_key(self.horizontal_crs), _crs_key(self.vertical_crs), self.geoid_model,
                 self.coordinate_epoch)
                == (_crs_key(other.horizontal_crs), _crs_key(other.vertical_crs), other.geoid_model,
                    other.coordinate_epoch))


@dataclass(frozen=True)
class ProjectedOrigin:
    """The site origin as a point in a projected CRS (`location.site_origin.projected`)."""

    crs: dict
    x: float
    y: float
    z: float
    unit: str
    z_reference: str

    @property
    def unit_to_metres(self) -> float:
        return _ORIGIN_UNITS[self.unit]


@dataclass(frozen=True)
class SiteOrigin:
    """`location.site_origin`: the one point every offset file is measured from."""

    latitude: float
    longitude: float
    ellipsoidal_height_m: float
    datum_height_m: float
    vertical_datum: str
    projected: ProjectedOrigin | None = None
    geodetic_crs: dict | None = None


def _site_origin(location: object) -> tuple[SiteOrigin | None, str | None]:
    raw = location.get("site_origin") if isinstance(location, dict) else None
    if raw is None:
        return None, None
    try:
        if not isinstance(raw, dict):
            raise ValueError("it is not an object")
        projected = raw.get("projected")
        point = None
        if projected is not None:
            if not isinstance(projected, dict) or _crs_key(projected.get("crs")) is None:
                raise ValueError("projected has no CRS reference")
            if not all(_number(projected.get(k)) for k in ("x", "y", "z")):
                raise ValueError("projected x, y and z are not all numbers")
            if not isinstance(projected.get("unit"), str) or not isinstance(
                    projected.get("z_reference"), str):
                raise ValueError("projected has no unit or z_reference")
            point = ProjectedOrigin(dict(projected["crs"]), float(projected["x"]),
                                    float(projected["y"]), float(projected["z"]),
                                    projected["unit"], projected["z_reference"])
        for key in ("latitude", "longitude", "ellipsoidal_height_m", "datum_height_m"):
            if not _number(raw.get(key)):
                raise ValueError(f"{key} is not a number")
        return SiteOrigin(float(raw["latitude"]), float(raw["longitude"]),
                          float(raw["ellipsoidal_height_m"]), float(raw["datum_height_m"]),
                          str(raw.get("vertical_datum", "")), point,
                          dict(raw["geodetic_crs"]) if isinstance(raw.get("geodetic_crs"), dict)
                          else None), None
    except ValueError as exc:
        return None, f"location.site_origin is unreadable ({exc}); offset files cannot be placed"


@dataclass(frozen=True)
class BundleFile:
    """One file of the bundle with what the manifest states about it."""

    path: str
    size: int | None
    #: The file's frame, or None when it states none or states one the reader cannot read.
    frame: Frame | None
    #: Why `frame` is None although the entry states one; None when it simply states none.
    frame_problem: str | None
    #: The `odbl_role` as written, None when the entry states none.
    declared_role: str | None
    license: str | None
    sources: tuple[dict, ...]
    entry: dict = field(default_factory=dict, repr=False)

    @property
    def role(self) -> str:
        """`database`, `produced-work` or `none`. A missing or unknown role reads as `database`."""
        return self.declared_role if self.declared_role in _ROLES else "database"

    @property
    def role_declared(self) -> bool:
        return self.declared_role in _ROLES


@dataclass(frozen=True)
class Offset:
    """The site origin as the offset of an offset-mode file, in the origin's own unit."""

    x: float
    y: float
    z: float
    unit: str
    unit_to_metres: float
    z_reference: str
    crs: dict


@dataclass(frozen=True)
class Transform:
    """A linear map between two files' coordinates: `dst = matrix @ src + translation`.

    The translation is in the destination file's units.
    """

    matrix: Matrix
    translation: Vector

    def apply(self, point: Vector) -> Vector:
        moved = _apply(self.matrix, tuple(float(c) for c in point))  # type: ignore[arg-type]
        return tuple(m + t for m, t in zip(moved, self.translation))  # type: ignore[return-value]


@dataclass(frozen=True)
class Placement:
    """How one file's coordinates sit in its frame, without leaving the frame.

    `axis_transform` takes a file point to east-north-up; the frame's units scale it to metres
    (`frame.unit_to_metres`, `frame.vertical_unit_to_metres`). An `offset`-mode file also has
    an `offset`: the site origin, in its projected CRS, which `to_enu_metres` adds. Where that
    cannot be had without reprojecting (no `projected` block, or one in another CRS than the
    file's), `offset` is None and `offset_problem` says why.
    """

    path: str
    frame: Frame
    offset: Offset | None
    offset_problem: str | None

    @property
    def mode(self) -> str:
        return self.frame.coordinate_mode

    @property
    def axis_transform(self) -> Matrix:
        return self.frame.axis_transform

    def axes_to_enu(self, point: Vector) -> Vector:
        """A file point turned onto east-north-up in the file's own units; no scale, no offset."""
        return _apply(self.axis_transform, tuple(point))  # type: ignore[arg-type]

    def to_enu_metres(self, point: Vector) -> Vector:
        """A file point as east, north, up in metres in the frame's horizontal CRS.

        An absolute file is its own coordinates; an offset file is the origin plus its point.
        Raises `FrameError` for an offset file whose origin is not available in this frame.
        """
        east, north, up = self.axes_to_enu(point)
        east *= self.frame.unit_to_metres
        north *= self.frame.unit_to_metres
        up *= self.frame.vertical_unit_to_metres
        if self.mode == "offset":
            origin = self._origin_metres()
            return (origin[0] + east, origin[1] + north, origin[2] + up)
        return (east, north, up)

    def _origin_metres(self) -> Vector:
        if self.offset is None:
            raise FrameError(f"{self.path}: {self.offset_problem}")
        o = self.offset
        return (o.x * o.unit_to_metres, o.y * o.unit_to_metres, o.z * o.unit_to_metres)

    def _to_metres(self) -> Matrix:
        h, v = self.frame.unit_to_metres, self.frame.vertical_unit_to_metres
        return _multiply(((h, 0, 0), (0, h, 0), (0, 0, v)), self.axis_transform)


def _offset_for(frame: Frame, origin: SiteOrigin | None,
                origin_problem: str | None) -> tuple[Offset | None, str | None]:
    if frame.coordinate_mode != "offset":
        return None, None
    if origin is None:
        return None, origin_problem or ("the manifest carries no location.site_origin to measure "
                                        "the offsets from")
    point = origin.projected
    if point is None:
        return None, ("location.site_origin has no projected point; placing this file would take "
                      "a reprojection of its latitude and longitude, which this reader never does")
    if point.unit not in _ORIGIN_UNITS:
        return None, (f"location.site_origin.projected.unit {point.unit!r} is not one of "
                      f"{', '.join(_ORIGIN_UNITS)}")
    if _crs_key(point.crs) != _crs_key(frame.horizontal_crs):
        return None, ("location.site_origin.projected is in another CRS than the file's "
                      "horizontal_crs; relating them would take a reprojection, which this reader "
                      "never does")
    return Offset(point.x, point.y, point.z, point.unit, point.unit_to_metres, point.z_reference,
                  dict(point.crs)), None


@dataclass(frozen=True)
class FrameGroup:
    """The files that share one frame (`Frame.same_frame_as`)."""

    frame: Frame
    files: tuple[BundleFile, ...]


class BundleReader:
    """An open bundle: its manifest, every file with frame and role, and the placement helpers."""

    def __init__(self, bundle: object, manifest: dict) -> None:
        self._bundle = bundle
        self.manifest = manifest
        self.form = bundle.form  # type: ignore[attr-defined]
        self.problems: list[str] = []
        self.site_origin, problem = _site_origin(manifest.get("location"))
        self._origin_problem = problem
        if problem:
            self.problems.append(problem)
        entries = manifest.get("file_entries")
        entries = entries if isinstance(entries, dict) else {}
        names = bundle.files  # type: ignore[attr-defined]
        self._files = {name: self._file(name, entries.get(name)) for name in names}
        self.unmatched_entries = sorted(k for k in entries if k not in self._files)
        for name in self.unmatched_entries:
            self.problems.append(f"file_entries names {name}, which this bundle does not hold")

    def _file(self, name: str, raw: object) -> BundleFile:
        entry = raw if isinstance(raw, dict) else {}
        frame, problem = None, None
        if "frame" in entry:
            try:
                frame = Frame.from_entry(entry["frame"])
            except FrameError as exc:
                problem = f"{name}: {exc}"
        role = entry.get("odbl_role")
        sources = entry.get("sources")
        return BundleFile(
            path=name, size=self._bundle.size(name),  # type: ignore[attr-defined]
            frame=frame, frame_problem=problem,
            declared_role=role if isinstance(role, str) else None,
            license=entry.get("license") if isinstance(entry.get("license"), str) else None,
            sources=tuple(s for s in sources if isinstance(s, dict)) if isinstance(sources, list)
            else (), entry=entry)

    @property
    def version(self) -> object:
        return self.manifest.get("version")

    @property
    def files(self) -> list[BundleFile]:
        """Every file in the bundle, by path: the manifest and README included."""
        return [self._files[name] for name in sorted(self._files)]

    def __iter__(self) -> Iterator[BundleFile]:
        return iter(self.files)

    def __len__(self) -> int:
        return len(self._files)

    def file(self, path: str) -> BundleFile:
        try:
            return self._files[path]
        except KeyError:
            raise KeyError(f"{path} is not a file of this bundle") from None

    def by_frame(self) -> list[FrameGroup]:
        """The files that state a readable frame, grouped by the frame they share."""
        groups: list[tuple[Frame, list[BundleFile]]] = []
        for f in self.files:
            if f.frame is None:
                continue
            for frame, members in groups:
                if frame.same_frame_as(f.frame):
                    members.append(f)
                    break
            else:
                groups.append((f.frame, [f]))
        return [FrameGroup(frame, tuple(members)) for frame, members in groups]

    def unframed(self) -> list[BundleFile]:
        """The files with no readable frame: none stated, or one the reader refuses."""
        return [f for f in self.files if f.frame is None]

    def placement(self, path: str) -> Placement:
        """How `path` sits in its own frame. Raises `FrameError` for a file with no readable frame."""
        found = self.file(path)
        if found.frame is None:
            raise FrameError(found.frame_problem or f"{path} states no frame, so it cannot be placed")
        offset, problem = _offset_for(found.frame, self.site_origin, self._origin_problem)
        return Placement(path, found.frame, offset, problem)

    def transform(self, source: str, destination: str) -> Transform:
        """The map from `source` file coordinates to `destination` file coordinates.

        Both files must be in one frame (`FrameMismatch` otherwise: no reprojection). The result
        is axes, units and, between an absolute and an offset file, the site origin added.
        """
        src, dst = self.placement(source), self.placement(destination)
        if not src.frame.same_frame_as(dst.frame):
            raise FrameMismatch(
                f"{source} and {destination} are in different frames (CRS, vertical CRS, geoid or "
                "epoch differ); relating them would take a reprojection, which this reader never "
                "does")
        origin: Vector = (0.0, 0.0, 0.0)
        if src.mode != dst.mode:
            offset_side = src if src.mode == "offset" else dst
            origin = offset_side._origin_metres()
            assert offset_side.offset is not None
            if offset_side.offset.z_reference != "vertical_datum":
                raise FrameError(
                    f"the site origin height is {offset_side.offset.z_reference}, but "
                    f"{'absolute' if offset_side is dst else 'offset'}-mode heights are in the "
                    "file's vertical CRS; joining them would take a geoid model this reader does "
                    "not apply")
        # metres_src = O_src + A_src p;  metres_dst = O_dst + A_dst q;  both are the same point.
        a_dst_inverse = _multiply(_transpose(dst.axis_transform),
                                  ((1 / dst.frame.unit_to_metres, 0, 0),
                                   (0, 1 / dst.frame.unit_to_metres, 0),
                                   (0, 0, 1 / dst.frame.vertical_unit_to_metres)))
        matrix = _multiply(a_dst_inverse, src._to_metres())
        shift = _signed_origin(src, dst, origin)
        translation = _apply(a_dst_inverse, shift)
        return Transform(matrix, translation)

    def open(self, path: str) -> IO[bytes]:
        self.file(path)
        return self._bundle.open(path)  # type: ignore[attr-defined]

    def read(self, path: str) -> bytes:
        self.file(path)
        return self._bundle.read(path)  # type: ignore[attr-defined]

    def close(self) -> None:
        self._bundle.close()  # type: ignore[attr-defined]

    def __enter__(self) -> "BundleReader":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def _signed_origin(src: Placement, dst: Placement, origin: Vector) -> Vector:
    """O_src - O_dst: the origin when only the source is offset, its negation when only the destination is."""
    if src.mode == dst.mode:
        return (0.0, 0.0, 0.0)
    sign = 1.0 if src.mode == "offset" else -1.0
    return tuple(sign * c for c in origin)  # type: ignore[return-value]


def read_bundle(path: str | Path) -> BundleReader:
    """Open the bundle at `path` (a zip, a folder, or either inside one wrapper folder).

    Raises `BundleError` when it is not a bundle: unreadable, no manifest, or a manifest that is
    not a JSON object. It does not check the bundle against the schema (that is `validate`).
    """
    report = Report(input=str(path))
    bundle = open_bundle(Path(path), report)
    if bundle is None:
        reasons = "; ".join(f.message for f in report.findings if f.severity == "error")
        raise BundleError(reasons or f"{path} is not a bundle")
    try:
        manifest = json.loads(bundle.read(MANIFEST_PATH).decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as exc:
        bundle.close()
        raise BundleError(f"{MANIFEST_PATH} is not UTF-8 JSON: {exc}") from None
    if not isinstance(manifest, dict):
        bundle.close()
        raise BundleError(f"{MANIFEST_PATH} is JSON but not an object")
    return BundleReader(bundle, manifest)
