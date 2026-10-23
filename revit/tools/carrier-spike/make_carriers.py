"""Write the two prototype carriers for one extracted bundle, the way the ETL ticket would emit them.

    python make_carriers.py <extracted bundle dir> <out dir>

Needs GDAL's ogr2ogr on PATH and ezdxf (pip install ezdxf). It stands in for the pipeline only to
give the spike two carriers holding the same features as today's GeoJSON: the Revit plugin itself
never reprojects, and the one reprojection here (the footprints, which no Revit GeoJSON carries
today) is GDAL's, on the ETL's side of the line. Writes SiteContext.gpkg and SiteContext.dxf, and
footprints.geojson, the footprints in the order's CRS, so the probe can compare all three.
"""

import json
import os
import subprocess
import sys

import ezdxf

APPLICATION = "MANTLEPLACE"
FOOTPRINTS = "building_footprints"


def main(bundle: str, out: str) -> None:
    os.makedirs(out, exist_ok=True)
    manifest = json.load(open(os.path.join(bundle, "Metadata", "manifest.json"), encoding="utf-8"))
    revit = manifest["hosts"]["revit"]
    epsg = revit["georeference"]["origin"]["projected"]["epsg"]
    layers = [(layer["name"], os.path.join(bundle, layer["path"])) for layer in revit["vectors"]["layers"]]

    footprints = os.path.join(out, "footprints.geojson")
    run("ogr2ogr", "-f", "GeoJSON", "-t_srs", f"EPSG:{epsg}", "-lco", "COORDINATE_PRECISION=6",
        footprints, os.path.join(bundle, "Vector", "Building.geojson"))
    layers.append((FOOTPRINTS, footprints))

    gpkg = os.path.join(out, "SiteContext.gpkg")
    if os.path.exists(gpkg):
        os.remove(gpkg)
    for name, path in layers:
        mode = ["-update", "-append"] if os.path.exists(gpkg) else []
        run("ogr2ogr", "-f", "GPKG", *mode, "-a_srs", f"EPSG:{epsg}", "-nln", name, "-nlt", "GEOMETRY",
            "-dim", "XYZ" if has_z(path) else "XY", "-lco", "GEOMETRY_NAME=geom", "-lco", "SPATIAL_INDEX=NO", gpkg, path)

    write_dxf(layers, os.path.join(out, "SiteContext.dxf"))
    print(json.dumps({"epsg": epsg, "layers": [name for name, _ in layers], "gpkg": gpkg}))


def has_z(path: str) -> bool:
    for feature in json.load(open(path, encoding="utf-8"))["features"]:
        geometry = feature.get("geometry") or {}
        first = first_position(geometry.get("coordinates"))
        if first is not None:
            return len(first) > 2
    return False


def first_position(coordinates):
    while isinstance(coordinates, list) and coordinates and isinstance(coordinates[0], list):
        coordinates = coordinates[0]
    return coordinates if isinstance(coordinates, list) and coordinates else None


def write_dxf(layers, path: str) -> None:
    doc = ezdxf.new("R2010")
    doc.header["$INSUNITS"] = 21  # US survey feet: the order's unit, and nothing a reader can trust more than the manifest
    doc.appids.add(APPLICATION)
    msp = doc.modelspace()
    for name, source in layers:
        doc.layers.add(name)
        # Numbers stay the text the GeoJSON wrote, so "35.0" is still "35.0" on the far side.
        collection = json.load(open(source, encoding="utf-8"), parse_float=str, parse_int=str)
        polygons = 0
        for feature in collection["features"]:
            properties = feature.get("properties") or {}
            pairs = [(1000, f"{key}={value}") for key, value in properties.items()
                     if value is not None and not isinstance(value, (dict, list))]
            geometry = feature.get("geometry") or {}
            kind = geometry.get("type")
            coordinates = geometry.get("coordinates") or []
            if kind == "LineString":
                line(msp, name, coordinates, pairs)
            elif kind == "MultiLineString":
                for part in coordinates:
                    line(msp, name, part, pairs)
            elif kind in ("Polygon", "MultiPolygon"):
                for polygon in (coordinates if kind == "MultiPolygon" else [coordinates]):
                    polygons += 1
                    for index, ring in enumerate(polygon):
                        points = [(float(p[0]), float(p[1])) for p in ring]
                        if len(points) > 1 and points[0] == points[-1]:
                            points.pop()
                        entity = msp.add_lwpolyline(points, format="xy", close=True, dxfattribs={"layer": name})
                        entity.set_xdata(APPLICATION, pairs + [(1000, f"polygon={polygons}"),
                                                               (1000, "ring=" + ("hole" if index else "outer"))])
    doc.saveas(path)


def line(msp, layer: str, positions, pairs) -> None:
    if positions and len(positions[0]) > 2:
        entity = msp.add_polyline3d([tuple(float(v) for v in p[:3]) for p in positions], dxfattribs={"layer": layer})
    else:
        entity = msp.add_lwpolyline([(float(p[0]), float(p[1])) for p in positions], format="xy", dxfattribs={"layer": layer})
    entity.set_xdata(APPLICATION, pairs)


def run(*command: str) -> None:
    subprocess.run(command, check=True)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
