namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Reader behaviour that is this host's own rather than the shared contract's — and the regression
/// cases for defects found in adversarial review of the first tranche.
/// </summary>
internal static class ManifestReaderTests
{
    /// <summary>
    /// The shape of the real 2 km × 2 km metric purchase <c>a043aaeb-…</c>: a v19 manifest whose
    /// <c>delivery</c> block carries no <c>local_origin</c> at all, and whose origin is published in
    /// the <c>revit</c> block instead.
    /// </summary>
    private const string MetricTierOwnBlock = """
          {
            "version": "1.0.0",
            "delivery": {
              "unit_system": "metric",
              "tier": "metric",
              "horizontal_epsg": 32613,
              "linear_unit": "m"
            },
            "hosts": {
              "revit": {
                "georeference": {
                  "crs_projected": "EPSG:32613",
                  "crs_geographic": "EPSG:4326",
                  "vertical_datum": "EGM2008-orthometric",
                  "grid_rotation_deg": 0.0,
                  "is_projected": true,
                  "origin": {
                    "lon": -105.32557885004304,
                    "lat": 38.46130517000308,
                    "projected": {
                      "epsg": 32613,
                      "easting": 471594.99999999977,
                      "northing": 4257050.0,
                      "linear_unit": "m"
                    }
                  }
                }
              }
            }
          }
        """;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a readiness block alone means materialized, so its reasons survive (HPS-36)", () =>
        {
            // Regression: this manifest used to be refused outright, which threw away all three
            // stated reasons and left the user a generic "nothing here yet". Worse, adding an
            // unrelated `unreal` block made it behave correctly — Revit's compliance with its own
            // rule was contingent on another host being materialized.
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "hosts": {
                      "revit": {
                        "readiness": {
                          "toposurface_points": {
                            "present": false,
                            "reason": "points_csv_not_produced"
                          },
                          "ifc_site": {
                            "present": false,
                            "reason": "ifc_site_not_produced"
                          },
                          "surface_dxf": {
                            "present": false,
                            "reason": "surface_dxf_not_produced"
                          }
                        }
                      }
                    }
                  }
                """);

            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.False(manifest.HasRevitContent, "but carries nothing for Revit");
            run.Equal(manifest.Readiness.ToposurfacePoints.Reason, "points_csv_not_produced", "reason read");

            BundleImportPlan plan = BundleImportPlanner.Plan(manifest, ["README.md"], _ => null);
            run.False(plan.CanImport, "nothing to import");
            run.True(plan.Skipped.Count >= 3, $"every path is explained, got {plan.Skipped.Count}");
        });

        run.Case("a bundle materialized only for a host this plugin doesn't know is still readable", () =>
        {
            // The host-block list cannot be the only materialization signal — max/ and blender/ are
            // already anticipated in this repo's layout.
            BundleManifest manifest = BundleManifestReader.Parse(
                """{"version": "1.0.0", "hosts": {"max": {"readiness": {"mesh": {"present": true}}}}}""");
            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
        });

        run.Case("a true base bundle is still refused, with its facts intact (HPS-37)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "packaging": {
                      "delivery_model": "base_on_demand"
                    },
                    "layout": {
                      "cesium_terrain": "Elevation/Terrain/layer.json"
                    },
                    "order_id": "ord-1"
                  }
                """);

            run.False(manifest.IsValid, "refused");
            run.Equal(manifest.OrderId, "ord-1", "the vault join key survives the refusal");
            run.Equal(manifest.CesiumTerrainPath, "Elevation/Terrain/layer.json", "streaming path survives");
        });

        run.Case("metadata is not transplanted from a detail block naming a different file", () =>
        {
            // Regression: `layout` and the (still undeclared) detail block can drift. Pairing
            // B.csv's path with A.csv's units imports a metric file at 0.3048 m/unit — a site 3.28×
            // too small, with nothing on screen to suggest it.
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "points_csv": "Surface/B.csv"
                    },
                    "elevation": {
                      "points_csv": {
                        "path": "Surface/A.csv",
                        "units": "ftUS",
                        "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                      }
                    }
                  }
                """);

            run.Equal(manifest.ToposurfacePoints?.Path, "Surface/B.csv", "the declared pointer wins");
            run.True(manifest.ToposurfacePoints?.Units is null, "the other file's units are discarded");
            run.False(manifest.ToposurfacePoints?.IsSha256Known ?? true, "the other file's hash is discarded");
        });

        run.Case("agreeing pointers keep their metadata", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "points_csv": "Surface/SurfacePoints.csv"
                    },
                    "elevation": {
                      "points_csv": {
                        "path": "Surface/SurfacePoints.csv",
                        "units": "ftUS"
                      }
                    }
                  }
                """);

            run.Equal(manifest.ToposurfacePoints?.Units, "ftUS", "units kept when both name one file");
        });

        run.Case("a null bbox edge is unknown, not zero (HPS-20)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """{"version": "1.0.0", "hosts": {"unreal": {}}, "bbox": {"west": null, "south": 36.2, "east": -105.6, "north": 36.3}}""");
            run.False(manifest.HasBbox, "a partially-known bbox is not a bbox");

            BundleManifest complete = BundleManifestReader.Parse(
                """{"version": "1.0.0", "hosts": {"unreal": {}}, "bbox": {"west": -105.7, "south": 36.2, "east": -105.6, "north": 36.3}}""");
            run.True(complete.HasBbox, "a fully-known bbox is");
        });

        run.Case("a version that is not a semver string is refused, whatever its JSON type", () =>
        {
            // Replaces "the version gate truncates, matching the Unreal reference". That case
            // pinned truncate-not-round because a non-integral version like 17.6 would otherwise be
            // rounded up and accepted by one host while the other refused it — a divergence inside
            // a ⛔ gate. MPB versions are STRINGS, so there is no number to round and the
            // divergence is unrepresentable rather than merely policed.
            //
            // What has to hold now is that nothing outside the semver family is coerced into one.
            // A number is the whole integer pre-history and is refused wholesale; the near-misses
            // are refused because "close to a version" is how a reader ends up dual-parsing.
            string[] notVersions =
                ["19", "18.9", "\"19\"", "\"1.0\"", "\"1.0.0-rc1\"", "\"01.0.0\"", "null", "true"];
            foreach (string version in notVersions)
            {
                // Concatenated rather than interpolated: the literal ends in three consecutive
                // closing braces, which an interpolated raw string reads as a placeholder.
                BundleManifest manifest = BundleManifestReader.Parse(
                    """{"version": """ + version + """, "hosts": {"unreal": {}}}""");
                run.False(manifest.IsValid, $"{version} refused");
                run.Contains(manifest.Error, "no longer supported", $"{version} names the version gate");
            }

            run.True(
                BundleManifestReader.Parse("""{"version": "1.0.0", "hosts": {"unreal": {}}}""").IsValid,
                "the semver form is accepted");
        });

        run.Case("unit_system is recorded but does not gate the import", () =>
        {
            // Nothing reads UnitSystem — scale comes from linear_unit and per-artifact units. A
            // refusal here would make every bundle unimportable the day web adds a third token.
            BundleManifest manifest = BundleManifestReader.Parse(
                """{"version": "1.0.0", "hosts": {"unreal": {}}, "delivery": {"unit_system": "nautical", "linear_unit": "m"}}""");
            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.True(manifest.Delivery.UnitSystem == UnitSystem.Unspecified, "recorded as unspecified");
        });

        run.Case("linear_unit DOES gate the import, because scale depends on it (HPS-35)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """{"version": "1.0.0", "hosts": {"unreal": {}}, "delivery": {"linear_unit": "furlong"}}""");
            run.False(manifest.IsValid, "refused");
            run.Contains(manifest.Error, "furlong", "names the offending value");
        });

        run.Case("a present v19 revit deliverable surfaces its own hash (HPS-33, HPS-34)", () =>
        {
            // v19 publishes the Revit deliverables' hashes in the `revit` block and nowhere else —
            // `elevation.points_csv` still carries none. A host reads its OWN block (HPS-33).
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "points_csv": "Surface/SurfacePoints.csv"
                    },
                    "elevation": {
                      "points_csv": {
                        "path": "Surface/SurfacePoints.csv",
                        "units": "m"
                      }
                    },
                    "hosts": {
                      "revit": {
                        "toposurface_points": {
                          "path": "Surface/SurfacePoints.csv",
                          "units": "m",
                          "point_count": 98756,
                          "sha256": "3faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                        }
                      }
                    }
                  }
                """);

            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.Equal(
                manifest.ToposurfacePoints?.Sha256,
                "3faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "the block's hash is surfaced");
            run.True(manifest.ToposurfacePoints?.IsSha256Known ?? false, "so the artifact is verifiable");
        });

        run.Case("a present v19 revit deliverable with no sha256 is refused (HPS-34)", () =>
        {
            // The v19 schema makes sha256 REQUIRED on each present revit.* sub-object, so a missing
            // one is a producer bug. Importing unverifiable bytes is worse than not importing.
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "surface_dxf": "Surface/Surface.dxf"
                    },
                    "hosts": {
                      "revit": {
                        "surface_dxf": {
                          "path": "Surface/Surface.dxf",
                          "surf_type": "TIN-3DFACE",
                          "units": "m",
                          "triangle_count": 150000
                        }
                      }
                    }
                  }
                """);

            run.False(manifest.IsValid, "refused");
            run.Contains(manifest.Error, "revit.surface_dxf has no sha256", "names the offending block");
        });

        run.Case("below v19 an absent hash is valid-but-unverified, never corrupt", () =>
        {
            // The floor still accepts v18, which published no Revit hashes at all. The version gate
            // on the required-hash rule is what keeps those bundles importable.
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "points_csv": "Surface/SurfacePoints.csv",
                      "surface_dxf": "Surface/Surface.dxf"
                    },
                    "elevation": {
                      "points_csv": {
                        "path": "Surface/SurfacePoints.csv",
                        "units": "m"
                      },
                      "surface_dxf": {
                        "path": "Surface/Surface.dxf",
                        "units": "m"
                      }
                    }
                  }
                """);

            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.True(manifest.ToposurfacePoints is not null, "the artifact is present");
            run.False(manifest.ToposurfacePoints?.IsSha256Known ?? true, "and unverified, not corrupt");
            run.False(manifest.SurfaceDxf?.IsSha256Known ?? true, "the dxf likewise");
        });

        run.Case("a metric-tier bundle is georeferenced from this host's own block", () =>
        {
            // The defect this closes: `delivery.local_origin` is emitted on the `local_ft` tier
            // ALONE, so on the metric tier the reader found no survey point, the planner skipped
            // SetSharedCoordinates, and the model was imported not georeferenced at all — while the
            // origin sat in the manifest addressed to this host by name.
            BundleManifest manifest = BundleManifestReader.Parse(MetricTierOwnBlock);

            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.True(manifest.HasPreDerivedSurveyPoint, "the published origin IS the survey point");
            run.Equal(manifest.SurveyPoint?.Epsg ?? 0, 32613, "EPSG from the own block");
            run.Within(manifest.SurveyPoint?.Easting ?? 0.0, 471594.99999999977, 1e-6, "easting verbatim");
            run.Within(manifest.SurveyPoint?.Northing ?? 0.0, 4257050.0, 1e-6, "northing verbatim");
            run.True(manifest.SurveyPoint?.LinearUnit == LinearUnit.Metre, "the origin's own unit");
            run.Within(manifest.Georeference.GridRotationDeg ?? -1.0, 0.0, 1e-12, "rotation read, not assumed");
            run.Equal(manifest.Georeference.VerticalDatum, "EGM2008-orthometric", "vertical datum read");
        });

        run.Case("the own block wins over delivery.local_origin when both are published", () =>
        {
            // The ordering is what makes reading the own block a complete fix with no coordinated
            // pipeline release: a `local_ft` bundle carrying both is placed from the block addressed
            // to this host, and one carrying only the delivery origin still works (below).
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "delivery": {
                      "unit_system": "imperial",
                      "tier": "local_ft",
                      "linear_unit": "ft",
                      "local_origin": {
                        "lon": -105.6462,
                        "lat": 36.2725,
                        "utm_epsg": 32613,
                        "easting_m": 441959.5,
                        "northing_m": 4014372.5
                      }
                    },
                    "hosts": {
                      "revit": {
                        "georeference": {
                          "crs_projected": "EPSG:2231",
                          "grid_rotation_deg": 0.0,
                          "origin": {
                            "lon": -105.6462,
                            "lat": 36.2725,
                            "projected": {
                              "epsg": 2231,
                              "easting": 1450131.2,
                              "northing": 13171825.6,
                              "linear_unit": "ftUS"
                            }
                          }
                        }
                      }
                    }
                  }
                """);

            run.Equal(manifest.SurveyPoint?.Epsg ?? 0, 2231, "the own block's EPSG, not delivery's");
            run.Within(manifest.SurveyPoint?.Easting ?? 0.0, 1450131.2, 1e-6, "the own block's easting");
            run.True(
                manifest.SurveyPoint?.LinearUnit == LinearUnit.UsSurveyFoot,
                "and its own unit — a State-Plane foot origin read as metres is a site 3.28× out");
        });

        run.Case("delivery.local_origin still works when there is no own block", () =>
        {
            // The fallback. Every `local_ft` bundle already in a curator's hands predates the v19
            // block; dropping this path would break them to fix the metric tier.
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "delivery": {
                      "unit_system": "imperial",
                      "tier": "local_ft",
                      "linear_unit": "ft",
                      "local_origin": {
                        "lon": -105.6462,
                        "lat": 36.2725,
                        "utm_epsg": 32613,
                        "easting_m": 441959.5,
                        "northing_m": 4014372.5
                      }
                    }
                  }
                """);

            run.True(manifest.HasPreDerivedSurveyPoint, "the delivery origin is still applied");
            run.Equal(manifest.SurveyPoint?.Epsg ?? 0, 32613, "its EPSG");
            run.True(
                manifest.SurveyPoint?.LinearUnit == LinearUnit.Metre,
                "metric by field name — `easting_m`, whatever the block's artifact linear_unit says");
        });

        run.Case("a sibling host's georeference is never read for the survey point (HPS-33)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "hosts": {
                      "unreal": {
                        "georeference": {
                          "crs_projected": "EPSG:32613",
                          "origin": {
                            "utm": {
                              "epsg": 32613,
                              "easting": 441959.5,
                              "northing": 4014372.5
                            }
                          }
                        }
                      }
                    }
                  }
                """);

            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.False(
                manifest.HasPreDerivedSurveyPoint,
                "another host's origin is not this host's to apply — the honest outcome is no survey point");
        });

        run.Case("an unreadable origin linear_unit fails the bundle closed (HPS-35)", () =>
        {
            // Same rule as delivery.linear_unit, for the same reason: the origin positions
            // everything in the bundle, so a scale nobody can read is not one artifact's problem.
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "hosts": {
                      "revit": {
                        "georeference": {
                          "origin": {
                            "projected": {
                              "epsg": 32613,
                              "easting": 1.0,
                              "northing": 2.0,
                              "linear_unit": "furlong"
                            }
                          }
                        }
                      }
                    }
                  }
                """);

            run.False(manifest.IsValid, "refused");
            run.Contains(manifest.Error, "furlong", "names the offending value");
            run.Contains(
                manifest.Error,
                "revit.georeference.origin.projected.linear_unit",
                "and the field it came from, which is not delivery's");
        });

        run.Case("the imagery drape reads three host-neutral blocks and no host block", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "imagery_drape": "Imagery/Drape.png"
                    },
                    "imagery": {
                      "present": true,
                      "gsd_m": 0.3
                    },
                    "elevation": {
                      "dem": {
                        "crs": "EPSG:32613",
                        "bounds_target_crs": [
                          470880.0,
                          4256340.0,
                          472310.0,
                          4257760.0
                        ]
                      }
                    },
                    "hosts": {
                      "unreal": {
                        "imagery_drape": {
                          "source": "Imagery/Drape.png",
                          "extent_crs": "EPSG:32613",
                          "extent": [
                            1.0,
                            2.0,
                            3.0,
                            4.0
                          ],
                          "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                        }
                      }
                    }
                  }
                """);

            run.Equal(manifest.ImageryDrape?.Path, "Imagery/Drape.png", "the layout pointer is of record (HPS-32)");
            run.Within(manifest.ImageryGsdM ?? 0.0, 0.3, 1e-9, "the host-neutral GSD");
            run.Within(manifest.DemBounds?.Left ?? 0.0, 470880.0, 1e-6, "the DEM's own bounds");
            run.Equal(manifest.DemBounds?.Epsg ?? 0, 32613, "and their CRS");
            run.True(manifest.ImageryDrapeExtent is null, "no imagery.drape block means no drape extent");

            // The sibling host's block carries both an extent and a sha for this very file. Reading
            // either would be the read-a-sibling's-block failure exactly, so the drape stays

            // unverified instead.
            run.True(manifest.ImageryDrape?.Sha256 is null, "unreal.imagery_drape.sha256 is not read");
        });

        run.Case("the drape's own block is read when a bundle carries one", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                  {
                    "version": "1.0.0",
                    "layout": {
                      "imagery_drape": "Imagery/Drape.png"
                    },
                    "imagery": {
                      "present": true,
                      "gsd_m": 0.3,
                      "drape": {
                        "extent": [
                          470880.0,
                          4256340.0,
                          472310.0,
                          4257760.0
                        ],
                        "extent_crs": "EPSG:32613",
                        "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                      }
                    }
                  }
                """);

            run.Within(manifest.ImageryDrapeExtent?.WidthUnits ?? 0.0, 1430.0, 1e-6, "the drape's own extent");
            run.Equal(manifest.ImageryDrapeExtent?.Epsg ?? 0, 32613, "in its own stated CRS");
            run.Contains(manifest.ImageryDrape?.Sha256, "dddd", "and a hash this host IS allowed to read");
        });

        run.Case("a malformed extent is unknown, never partially read", () =>
        {
            foreach (string bounds in (string[])["[1.0, 2.0, 3.0]", "[1.0, 2.0, 3.0, 4.0, 5.0]", "[1.0, \"x\", 3.0, 4.0]"])
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    """{"version": 19, "elevation": {"dem": {"crs": "EPSG:32613", "bounds_target_crs": """
                    + bounds
                    + "}}}");

                run.True(manifest.DemBounds is null, $"{bounds} yields no extent");
            }
        });

        run.Case("an absent imagery block is not an imagery block saying no", () =>
        {
            run.False(
                BundleManifestReader.Parse("""{"version": "1.0.0"}""").ImageryAbsentByDeclaration,
                "silence is unknown");
            run.True(
                BundleManifestReader.Parse("""{"version": "1.0.0", "imagery": {"present": false}}""")
                    .ImageryAbsentByDeclaration,
                "an explicit false is a statement");
        });

        return run.Report("manifest reader");
    }
}
