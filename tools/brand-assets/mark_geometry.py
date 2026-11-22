import re
import xml.etree.ElementTree as ElementTree
from typing import List, NamedTuple, Optional, Tuple

SOURCE_NAME = "mark.svg"

MANTLE_ORANGE = (0xFF, 0x71, 0x10)
WHITE = (0xFF, 0xFF, 0xFF)
BLACK = (0x00, 0x00, 0x00)

HOST_SIZES = (16, 24, 32, 48, 64)
UNREAL_SMALL_SIZE = 64
UNREAL_ICON_SIZE = 128

REVIT_RESOURCES = "revit/src/MantlePlace.Revit.Addin/Resources"
UNREAL_RESOURCES = "unreal/MantlePlace/Resources"

CURVE_SEGMENTS = 16

Point = Tuple[float, float]
Polygon = List[Point]


class Target(NamedTuple):
    path: str
    size: int


def targets() -> List[Target]:
    plan = [Target(f"{REVIT_RESOURCES}/MantlePlaceMark_{size}.png", size) for size in HOST_SIZES]
    plan.append(Target(f"{UNREAL_RESOURCES}/MantlePlaceTabIcon.png", UNREAL_SMALL_SIZE))
    plan.append(Target(f"{UNREAL_RESOURCES}/Icon128.png", UNREAL_ICON_SIZE))
    return plan


class Layer(NamedTuple):
    kind: str
    fill: Tuple[int, int, int]
    polygons: List[Polygon]
    circle: Optional[Tuple[float, float, float]]


class Mark(NamedTuple):
    view: Tuple[float, float]
    clip: Tuple[float, float, float]
    layers: List[Layer]


_NUMBER = r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"
_TOKEN = re.compile(rf"([A-Za-z])|({_NUMBER})")
_ARITY = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Z": 0}


def numbers(text: str) -> List[float]:
    return [float(match) for match in re.findall(_NUMBER, text)]


def _tokens(d: str):
    for letter, number in _TOKEN.findall(d):
        yield letter if letter else float(number)


def _cubic(p0: Point, p1: Point, p2: Point, p3: Point, segments: int) -> Polygon:
    points = []
    for step in range(1, segments):
        t = step / segments
        mt = 1 - t
        a, b, c, e = mt * mt * mt, 3 * mt * mt * t, 3 * mt * t * t, t * t * t
        points.append(
            (
                a * p0[0] + b * p1[0] + c * p2[0] + e * p3[0],
                a * p0[1] + b * p1[1] + c * p2[1] + e * p3[1],
            )
        )
    points.append(p3)
    return points


def path_polygons(d: str, segments: int = CURVE_SEGMENTS) -> List[Polygon]:
    polygons: List[Polygon] = []
    current: Polygon = []
    x = y = 0.0
    start = (0.0, 0.0)
    command = None
    last_control: Optional[Point] = None
    args: List[float] = []

    def finish():
        nonlocal current
        if len(current) > 1:
            polygons.append(current)
        current = []

    def run(cmd: str, values: List[float]):
        nonlocal x, y, start, current, last_control
        upper = cmd.upper()
        relative = cmd.islower()
        ox, oy = (x, y) if relative else (0.0, 0.0)
        control = None
        if upper == "M":
            finish()
            x, y = ox + values[0], oy + values[1]
            start = (x, y)
            current = [start]
        elif upper == "L":
            x, y = ox + values[0], oy + values[1]
            current.append((x, y))
        elif upper == "H":
            x = ox + values[0]
            current.append((x, y))
        elif upper == "V":
            y = oy + values[0]
            current.append((x, y))
        elif upper in ("C", "S"):
            if upper == "C":
                c1 = (ox + values[0], oy + values[1])
                rest = values[2:]
            else:
                c1 = (2 * x - last_control[0], 2 * y - last_control[1]) if last_control else (x, y)
                rest = values
            c2 = (ox + rest[0], oy + rest[1])
            end = (ox + rest[2], oy + rest[3])
            current.extend(_cubic((x, y), c1, c2, end, segments))
            x, y = end
            control = c2
        elif upper == "Z":
            finish()
            x, y = start
        last_control = control

    for token in _tokens(d):
        if isinstance(token, str):
            if token.upper() not in _ARITY:
                raise ValueError(f"The mark's paths use only {''.join(_ARITY)}; found {token}.")
            if args:
                raise ValueError(f"{len(args)} stray numbers before {token}.")
            command = token
            if token.upper() == "Z":
                run(token, [])
            continue
        if command is None:
            raise ValueError("A path must start with a command.")
        args.append(token)
        if len(args) == _ARITY[command.upper()]:
            run(command, args)
            args = []
            if command == "M":
                command = "L"
            elif command == "m":
                command = "l"
    if args:
        raise ValueError(f"{len(args)} numbers left over at the end of the path.")
    finish()
    return polygons


def _hex(value: str) -> Tuple[int, int, int]:
    digits = value.strip().lstrip("#")
    if len(digits) == 3:
        digits = "".join(ch * 2 for ch in digits)
    return (int(digits[0:2], 16), int(digits[2:4], 16), int(digits[4:6], 16))


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _circle(element) -> Tuple[float, float, float]:
    return (float(element.get("cx", 0)), float(element.get("cy", 0)), float(element.get("r")))


def read_mark(svg: str) -> Mark:
    root = ElementTree.fromstring(svg)
    view = numbers(root.get("viewBox", ""))
    if len(view) != 4:
        raise ValueError("The mark needs a viewBox.")

    classes = {}
    clips = {}
    for element in root.iter():
        name = _local(element.tag)
        if name == "style":
            for cls, fill in re.findall(r"\.([\w-]+)\s*\{[^}]*?fill:\s*(#[0-9a-fA-F]{3,6})", element.text or ""):
                classes[cls] = _hex(fill)
        elif name == "clipPath":
            shape = next((child for child in element if _local(child.tag) == "circle"), None)
            if shape is not None:
                clips[element.get("id")] = _circle(shape)

    clipped = next((e for e in root.iter() if e.get("clip-path")), None)
    if clipped is None:
        raise ValueError("The mark must be drawn inside a clip.")
    clip_id = re.match(r"url\(#([^)]+)\)", clipped.get("clip-path")).group(1)
    if clip_id not in clips:
        raise ValueError(f"The clip {clip_id} is not a circle.")

    layers = []
    for element in clipped.iter():
        name = _local(element.tag)
        if name not in ("path", "circle"):
            continue
        fill = classes.get(element.get("class"), BLACK)
        if element.get("fill"):
            fill = _hex(element.get("fill"))
        if name == "path":
            layers.append(Layer("path", fill, path_polygons(element.get("d")), None))
        else:
            layers.append(Layer("circle", fill, [], _circle(element)))

    return Mark((view[2], view[3]), clips[clip_id], layers)
