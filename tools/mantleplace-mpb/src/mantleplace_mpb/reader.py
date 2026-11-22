# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import IO, Iterator

from .archive import open_bundle
from .report import Report

Vector = tuple[float, float, float]
Matrix = tuple[Vector, Vector, Vector]

_ORIGIN_UNITS = {"m": 1.0, "ft": 0.3048, "usft": 1200 / 3937}
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
    pass


class FrameError(Exception):
    pass


class FrameMismatch(FrameError):
    pass


def _multiply(a: Matrix, b: Matrix) -> Matrix:
    rows = tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))
    return rows  # type: ignore[return-value]


def _transpose(a: Matrix) -> Matrix:
    return tuple(tuple(a[j][i] for j in range(3)) for i in range(3))  # type: ignore[return-value]


def _apply(a: Matrix, v: Vector) -> Vector:
    return tuple(sum(a[i][k] * v[k] for k in range(3)) for i in range(3))  # type: ignore[return-value]


def _crs_key(reference: object) -> tuple[str, str] | None:
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
    horizontal_crs: dict
    unit_to_metres: float
    axis_order: str
    axis_mapping: dict
    coordinate_mode: str
    vertical_crs: dict | None = None
    vertical_unit_to_metres: float = 1.0
    geoid_model: str | None = None
    pixel_convention: str | None = None
    row_order: str | None = None
    coordinate_epoch: float | None = None

    @classmethod
    def from_entry(cls, raw: object) -> "Frame":
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
        columns = [_DIRECTIONS[self.axis_mapping[a]] for a in ("x", "y", "z")]
        return tuple(tuple(columns[j][i] for j in range(3)) for i in range(3))  # type: ignore[return-value]

    def same_frame_as(self, other: "Frame") -> bool:
        return ((_crs_key(self.horizontal_crs), _crs_key(self.vertical_crs), self.geoid_model,
                 self.coordinate_epoch)
                == (_crs_key(other.horizontal_crs), _crs_key(other.vertical_crs), other.geoid_model,
                    other.coordinate_epoch))


@dataclass(frozen=True)
class ProjectedOrigin:
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
    path: str
    size: int | None
    frame: Frame | None
    frame_problem: str | None
    declared_role: str | None
    license: str | None
    sources: tuple[dict, ...]
    entry: dict = field(default_factory=dict, repr=False)

    @property
    def role(self) -> str:
        return self.declared_role if self.declared_role in _ROLES else "database"

    @property
    def role_declared(self) -> bool:
        return self.declared_role in _ROLES


@dataclass(frozen=True)
class Offset:
    x: float
    y: float
    z: float
    unit: str
    unit_to_metres: float
    z_reference: str
    crs: dict


@dataclass(frozen=True)
class Transform:
    matrix: Matrix
    translation: Vector

    def apply(self, point: Vector) -> Vector:
        moved = _apply(self.matrix, tuple(float(c) for c in point))  # type: ignore[arg-type]
        return tuple(m + t for m, t in zip(moved, self.translation))  # type: ignore[return-value]


@dataclass(frozen=True)
class Placement:
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
        return _apply(self.axis_transform, tuple(point))  # type: ignore[arg-type]

    def to_enu_metres(self, point: Vector) -> Vector:
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
    frame: Frame
    files: tuple[BundleFile, ...]


class BundleReader:
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
        return [f for f in self.files if f.frame is None]

    def placement(self, path: str) -> Placement:
        found = self.file(path)
        if found.frame is None:
            raise FrameError(found.frame_problem or f"{path} states no frame, so it cannot be placed")
        offset, problem = _offset_for(found.frame, self.site_origin, self._origin_problem)
        return Placement(path, found.frame, offset, problem)

    def transform(self, source: str, destination: str) -> Transform:
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
    if src.mode == dst.mode:
        return (0.0, 0.0, 0.0)
    sign = 1.0 if src.mode == "offset" else -1.0
    return tuple(sign * c for c in origin)  # type: ignore[return-value]


def read_bundle(path: str | Path) -> BundleReader:
    report = Report(input=str(path))
    bundle = open_bundle(Path(path), report)
    if bundle is None:
        reasons = "; ".join(f.message for f in report.findings if f.severity == "error")
        raise BundleError(reasons or f"{path} is not a bundle")
    try:
        manifest = json.loads(bundle.read(bundle.manifest_path).decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as exc:
        bundle.close()
        raise BundleError(f"{bundle.manifest_path} is not UTF-8 JSON: {exc}") from None
    if not isinstance(manifest, dict):
        bundle.close()
        raise BundleError(f"{bundle.manifest_path} is JSON but not an object")
    return BundleReader(bundle, manifest)
