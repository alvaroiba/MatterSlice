/*
This file is part of MatterSlice. A commandline utility for
generating 3D printing GCode.

Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using MSClipperLib;

namespace MatterHackers.MatterSlice
{
	using System;
	using System.Diagnostics;
	using System.IO;
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;

	public class LayerDataStorage
	{
		public List<ExtruderLayers> Extruders = new List<ExtruderLayers>();
		public IntPoint modelSize, modelMin, modelMax;
		public Polygons raftOutline = new Polygons();

		public Polygons Skirt { get; private set; } = new Polygons();
		public Polygons Brims { get; private set; } = new Polygons();

		public NewSupport Support = null;
		public List<Polygons> WipeShield { get; set; } = new List<Polygons>();
		public Polygons WipeTower = new Polygons();

		private int primesThisLayer = 0;

		public void CreateIslandData()
		{
			for (int extruderIndex = 0; extruderIndex < Extruders.Count; extruderIndex++)
			{
				Extruders[extruderIndex].CreateIslandData();
			}
		}

		public void CreateWipeShield(int totalLayers, ConfigSettings config)
		{
			if (config.WipeShieldDistanceFromShapes_um <= 0)
			{
				return;
			}

			for (int layerIndex = 0; layerIndex < totalLayers; layerIndex++)
			{
				Polygons wipeShield = new Polygons();
				for (int extruderIndex = 0; extruderIndex < this.Extruders.Count; extruderIndex++)
				{
					for (int islandIndex = 0; islandIndex < this.Extruders[extruderIndex].Layers[layerIndex].Islands.Count; islandIndex++)
					{
						wipeShield = wipeShield.CreateUnion(this.Extruders[extruderIndex].Layers[layerIndex].Islands[islandIndex].IslandOutline.Offset(config.WipeShieldDistanceFromShapes_um));
					}
				}
				this.WipeShield.Add(wipeShield);
			}

			for (int layerIndex = 0; layerIndex < totalLayers; layerIndex++)
			{
				this.WipeShield[layerIndex] = this.WipeShield[layerIndex].Offset(-1000).Offset(1000);
			}

			long offsetAngle_um = (long)(Math.Tan(60.0 * Math.PI / 180) * config.LayerThickness_um);//Allow for a 60deg angle in the wipeShield.
			for (int layerIndex = 1; layerIndex < totalLayers; layerIndex++)
			{
				this.WipeShield[layerIndex] = this.WipeShield[layerIndex].CreateUnion(this.WipeShield[layerIndex - 1].Offset(-offsetAngle_um));
			}

			for (int layerIndex = totalLayers - 1; layerIndex > 0; layerIndex--)
			{
				this.WipeShield[layerIndex - 1] = this.WipeShield[layerIndex - 1].CreateUnion(this.WipeShield[layerIndex].Offset(-offsetAngle_um));
			}
		}

		public void CreateWipeTower(int totalLayers, ConfigSettings config)
		{
			if (config.WipeTowerSize_um < 1
				|| LastLayerWithChange(config) == -1)
			{
				return;
			}

			Polygon wipeTowerShape = new Polygon();
			wipeTowerShape.Add(new IntPoint(this.modelMin.X - 3000, this.modelMax.Y + 3000));
			wipeTowerShape.Add(new IntPoint(this.modelMin.X - 3000, this.modelMax.Y + 3000 + config.WipeTowerSize_um));
			wipeTowerShape.Add(new IntPoint(this.modelMin.X - 3000 - config.WipeTowerSize_um, this.modelMax.Y + 3000 + config.WipeTowerSize_um));
			wipeTowerShape.Add(new IntPoint(this.modelMin.X - 3000 - config.WipeTowerSize_um, this.modelMax.Y + 3000));

			this.WipeTower.Add(wipeTowerShape);
			var wipeTowerBounds = wipeTowerShape.GetBounds();

			config.WipeCenter_um = new IntPoint(
				wipeTowerBounds.minX + (wipeTowerBounds.maxX - wipeTowerBounds.minX) / 2,
				wipeTowerBounds.minY + (wipeTowerBounds.maxY - wipeTowerBounds.minY) / 2);
		}

		public void DumpLayerparts(string filename)
		{
			StreamWriter streamToWriteTo = new StreamWriter(filename);
			streamToWriteTo.Write("<!DOCTYPE html><html><body>");

			for (int extruderIndex = 0; extruderIndex < this.Extruders.Count; extruderIndex++)
			{
				for (int layerNr = 0; layerNr < this.Extruders[extruderIndex].Layers.Count; layerNr++)
				{
					streamToWriteTo.Write("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" style=\"width: 500px; height:500px\">\n");
					SliceLayer layer = this.Extruders[extruderIndex].Layers[layerNr];
					for (int i = 0; i < layer.Islands.Count; i++)
					{
						LayerIsland part = layer.Islands[i];
						for (int j = 0; j < part.IslandOutline.Count; j++)
						{
							streamToWriteTo.Write("<polygon points=\"");
							for (int k = 0; k < part.IslandOutline[j].Count; k++)
								streamToWriteTo.Write("{0},{1} ".FormatWith((float)(part.IslandOutline[j][k].X - modelMin.X) / modelSize.X * 500, (float)(part.IslandOutline[j][k].Y - modelMin.Y) / modelSize.Y * 500));
							if (j == 0)
								streamToWriteTo.Write("\" style=\"fill:gray; stroke:black;stroke-width:1\" />\n");
							else
								streamToWriteTo.Write("\" style=\"fill:red; stroke:black;stroke-width:1\" />\n");
						}
					}
					streamToWriteTo.Write("</svg>\n");
				}
			}
			streamToWriteTo.Write("</body></html>");
			streamToWriteTo.Close();
		}

		[Conditional("DEBUG")]
		private void CheckNoExtruderPrimed(ConfigSettings config)
		{
			if (primesThisLayer > 0)
			{
				throw new Exception("No extruders should be primed");
			}
		}

		public void EnsureWipeTowerIsSolid(int layerIndex, LayerGCodePlanner gcodeLayer, GCodePathConfig fillConfig, ConfigSettings config)
		{
			if (layerIndex >= LastLayerWithChange(config))
			{
				return;
			}

			// TODO: if layer index == 0 do all the loops from the outside-in, in order (no lines should be in the wipe tower)
			if(layerIndex == 0)
			{
				CheckNoExtruderPrimed(config);

				long insetPerLoop = fillConfig.LineWidthUM;
				int maxPrimingLoops = MaxPrimingLoops(config);

				Polygons outlineForExtruder = this.WipeTower;

				var fillPolygons = new Polygons();
				while (outlineForExtruder.Count > 0)
				{
					for (int polygonIndex = 0; polygonIndex < outlineForExtruder.Count; polygonIndex++)
					{
						Polygon newInset = outlineForExtruder[polygonIndex];
						newInset.Add(newInset[0]); // add in the last move so it is a solid polygon
						fillPolygons.Add(newInset);
					}
					outlineForExtruder = outlineForExtruder.Offset(-insetPerLoop);
				}

				var oldPathFinder = gcodeLayer.PathFinder;
				gcodeLayer.PathFinder = null;
				gcodeLayer.QueuePolygons(fillPolygons, fillConfig);
				gcodeLayer.PathFinder = oldPathFinder;
			}
			else
			{
				// print all of the extruder loops that have not already been printed
				int maxPrimingLoops = MaxPrimingLoops(config);

				for (int primeLoop = primesThisLayer; primeLoop < maxPrimingLoops; primeLoop++)
				{
					// write the loops for this extruder, but don't change to it. We are just filling the prime tower.
					PrimeOnWipeTower(layerIndex, gcodeLayer, fillConfig, config, false);
				}

				// clear the history of printer extruders for the next layer
				primesThisLayer = 0;
			}
		}

		public void GenerateRaftOutlines(long extraDistanceAroundPart_um, ConfigSettings config)
		{
			LayerDataStorage storage = this;
			for (int extruderIndex = 0; extruderIndex < storage.Extruders.Count; extruderIndex++)
			{
				if (config.ContinuousSpiralOuterPerimeter && extruderIndex > 0)
				{
					continue;
				}

				if (storage.Extruders[extruderIndex].Layers.Count < 1)
				{
					continue;
				}

				SliceLayer layer = storage.Extruders[extruderIndex].Layers[0];
				// let's find the first layer that has something in it for the raft rather than a zero layer
				if (layer.Islands.Count == 0 && storage.Extruders[extruderIndex].Layers.Count > 2)
				{
					layer = storage.Extruders[extruderIndex].Layers[1];
				}

				for (int partIndex = 0; partIndex < layer.Islands.Count; partIndex++)
				{
					if (config.ContinuousSpiralOuterPerimeter && partIndex > 0)
					{
						continue;
					}

					storage.raftOutline = storage.raftOutline.CreateUnion(layer.Islands[partIndex].IslandOutline.Offset(extraDistanceAroundPart_um));
				}
			}

			storage.raftOutline = storage.raftOutline.CreateUnion(storage.WipeTower.Offset(extraDistanceAroundPart_um));
			if (storage.WipeShield.Count > 0
				&& storage.WipeShield[0].Count > 0)
			{
				storage.raftOutline = storage.raftOutline.CreateUnion(storage.WipeShield[0].Offset(extraDistanceAroundPart_um));
			}
			if (storage.Support != null)
			{
				storage.raftOutline = storage.raftOutline.CreateUnion(storage.Support.GetBedOutlines().Offset(extraDistanceAroundPart_um));
			}
		}

		public void GenerateSkirt(long distance_um, long extrusionWidth_um, int numberOfLoops, int brimCount, long minLength_um, ConfigSettings config)
		{
			Polygons islandsToSkirtAround = GetSkirtBounds(config, this, (distance_um > 0), distance_um, extrusionWidth_um, brimCount);

			if (islandsToSkirtAround.Count > 0)
			{
				// Find convex hull for the skirt outline
				Polygons convexHull = new Polygons(new[] { islandsToSkirtAround.CreateConvexHull() });

				// Create skirt loops from the ConvexHull
				for (int skirtLoop = 0; skirtLoop < numberOfLoops; skirtLoop++)
				{
					long offsetDistance = distance_um + extrusionWidth_um * (skirtLoop - 1) - extrusionWidth_um / 2;

					this.Skirt.AddAll(convexHull.Offset(offsetDistance));

					int length = (int)this.Skirt.PolygonLength();
					if (skirtLoop + 1 >= numberOfLoops && length > 0 && length < minLength_um)
					{
						// add more loops for as long as we have not extruded enough length
						numberOfLoops++;
					}
				}
			}
		}

		private int MaxPrimingLoops(ConfigSettings config)
		{
			return Support != null ? config.ExtruderCount * 2 - 2 : config.ExtruderCount - 1;
		}

		public void GenerateWipeTowerInfill(int extruderIndex, Polygons partOutline, Polygons outputfillPolygons, long extrusionWidth_um, ConfigSettings config)
		{
			int maxPrimingLoops = MaxPrimingLoops(config);

			Polygons outlineForExtruder = partOutline.Offset(-extrusionWidth_um * extruderIndex);

			long insetPerLoop = extrusionWidth_um * maxPrimingLoops;
			while (outlineForExtruder.Count > 0)
			{
				for (int polygonIndex = 0; polygonIndex < outlineForExtruder.Count; polygonIndex++)
				{
					Polygon newInset = outlineForExtruder[polygonIndex];
					newInset.Add(newInset[0]); // add in the last move so it is a solid polygon
					outputfillPolygons.Add(newInset);
				}
				outlineForExtruder = outlineForExtruder.Offset(-insetPerLoop);
			}

			outputfillPolygons.Reverse();
		}

		public bool HaveWipeTower(ConfigSettings config)
		{
			if (config.WipeTowerSize_um == 0
				 || LastLayerWithChange(config) == -1)
			{
				return false;
			}

			return true;
		}

		public void PrimeOnWipeTower(int layerIndex, LayerGCodePlanner layerGcodePlanner, GCodePathConfig fillConfig, ConfigSettings config, bool airGapped)
		{
			if (!HaveWipeTower(config)
				|| layerIndex > LastLayerWithChange(config) + 1
				|| layerIndex == 0)
			{
				return;
			}

			if (airGapped)
			{
				// don't print the wipe tower with air gap height
				layerGcodePlanner.CurrentZ -= config.SupportAirGap_um;
			}

			//If we changed extruder, print the wipe/prime tower for this nozzle;
			Polygons fillPolygons = new Polygons();

			var oldPathFinder = layerGcodePlanner.PathFinder;
			layerGcodePlanner.PathFinder = null;
			GenerateWipeTowerInfill(primesThisLayer, this.WipeTower, fillPolygons, fillConfig.LineWidthUM, config);
			layerGcodePlanner.QueuePolygons(fillPolygons, fillConfig);
			layerGcodePlanner.PathFinder = oldPathFinder;

			if (airGapped)
			{
				// don't print the wipe tower with air gap height
				layerGcodePlanner.CurrentZ += config.SupportAirGap_um;
			}

			primesThisLayer++;
		}

		public void WriteRaftGCodeIfRequired(GCodeExport gcode, ConfigSettings config)
		{
			LayerDataStorage storage = this;
			if (config.ShouldGenerateRaft())
			{
				var raftBaseConfig = new GCodePathConfig("raftBaseConfig", "SUPPORT");
				raftBaseConfig.SetData(config.FirstLayerSpeed, config.RaftBaseExtrusionWidth_um);

				var raftMiddleConfig = new GCodePathConfig("raftMiddleConfig", "SUPPORT");
				raftMiddleConfig.SetData(config.RaftPrintSpeed, config.RaftInterfaceExtrusionWidth_um);

				var raftSurfaceConfig = new GCodePathConfig("raftMiddleConfig", "SUPPORT");
				raftSurfaceConfig.SetData((config.RaftSurfacePrintSpeed > 0) ? config.RaftSurfacePrintSpeed : config.RaftPrintSpeed, config.RaftSurfaceExtrusionWidth_um);

				// create the raft base
				{
					gcode.WriteComment("RAFT BASE");
					LayerGCodePlanner layerPlanner = new LayerGCodePlanner(config, gcode, config.TravelSpeed, config.MinimumTravelToCauseRetraction_um, config.PerimeterStartEndOverlapRatio);
					if (config.RaftExtruder >= 0)
					{
						// if we have a specified raft extruder use it
						layerPlanner.SetExtruder(config.RaftExtruder);
					}
					else if (config.SupportExtruder >= 0)
					{
						// else preserve the old behavior of using the support extruder if set.
						layerPlanner.SetExtruder(config.SupportExtruder);
					}

					gcode.CurrentZ_um = config.RaftBaseThickness_um;

					gcode.LayerChanged(-3, config.RaftBaseThickness_um);

					gcode.SetExtrusion(config.RaftBaseThickness_um, config.FilamentDiameter_um, config.ExtrusionMultiplier);

					// write the skirt around the raft
					layerPlanner.QueuePolygonsByOptimizer(storage.Skirt, null, raftBaseConfig, 0);

					List<Polygons> raftIslands = storage.raftOutline.ProcessIntoSeparateIslands();
					foreach (var raftIsland in raftIslands)
					{
						// write the outline of the raft
						layerPlanner.QueuePolygonsByOptimizer(raftIsland, null, raftBaseConfig, 0);

						Polygons raftLines = new Polygons();
						Infill.GenerateLinePaths(raftIsland.Offset(-config.RaftBaseExtrusionWidth_um) , raftLines, config.RaftBaseLineSpacing_um, config.InfillExtendIntoPerimeter_um, 0);

						// write the inside of the raft base
						layerPlanner.QueuePolygonsByOptimizer(raftLines, null, raftBaseConfig, 0);

						if (config.RetractWhenChangingIslands)
						{
							layerPlanner.ForceRetract();
						}
					}

					layerPlanner.WriteQueuedGCode(config.RaftBaseThickness_um);
				}

				// raft middle layers
				{
					gcode.WriteComment("RAFT MIDDLE");
					LayerGCodePlanner layerPlanner = new LayerGCodePlanner(config, gcode, config.TravelSpeed, config.MinimumTravelToCauseRetraction_um, config.PerimeterStartEndOverlapRatio);
					gcode.CurrentZ_um = config.RaftBaseThickness_um + config.RaftInterfaceThicknes_um;
					gcode.LayerChanged(-2, config.RaftInterfaceThicknes_um);
					gcode.SetExtrusion(config.RaftInterfaceThicknes_um, config.FilamentDiameter_um, config.ExtrusionMultiplier);

					Polygons raftLines = new Polygons();
					Infill.GenerateLinePaths(storage.raftOutline, raftLines, config.RaftInterfaceLineSpacing_um, config.InfillExtendIntoPerimeter_um, 45);
					layerPlanner.QueuePolygonsByOptimizer(raftLines, null, raftMiddleConfig, 0);

					layerPlanner.WriteQueuedGCode(config.RaftInterfaceThicknes_um);
				}

				for (int raftSurfaceIndex = 1; raftSurfaceIndex <= config.RaftSurfaceLayers; raftSurfaceIndex++)
				{
					gcode.WriteComment("RAFT SURFACE");
					LayerGCodePlanner layerPlanner = new LayerGCodePlanner(config, gcode, config.TravelSpeed, config.MinimumTravelToCauseRetraction_um, config.PerimeterStartEndOverlapRatio);
					gcode.CurrentZ_um = config.RaftBaseThickness_um + config.RaftInterfaceThicknes_um + config.RaftSurfaceThickness_um * raftSurfaceIndex;
					gcode.LayerChanged(-1, config.RaftSurfaceThickness_um);
					gcode.SetExtrusion(config.RaftSurfaceThickness_um, config.FilamentDiameter_um, config.ExtrusionMultiplier);

					Polygons raftLines = new Polygons();
					if (raftSurfaceIndex == config.RaftSurfaceLayers)
					{
						// make sure the top layer of the raft is 90 degrees offset to the first layer of the part so that it has minimum contact points.
						Infill.GenerateLinePaths(storage.raftOutline, raftLines, config.RaftSurfaceLineSpacing_um, config.InfillExtendIntoPerimeter_um, config.InfillStartingAngle + 90);
					}
					else
					{
						Infill.GenerateLinePaths(storage.raftOutline, raftLines, config.RaftSurfaceLineSpacing_um, config.InfillExtendIntoPerimeter_um, 90 * raftSurfaceIndex);
					}
					layerPlanner.QueuePolygonsByOptimizer(raftLines, null, raftSurfaceConfig, 0);

					layerPlanner.WriteQueuedGCode(config.RaftInterfaceThicknes_um);
				}
			}
		}

		private static Polygons GetSkirtBounds(ConfigSettings config, LayerDataStorage storage, bool externalOnly, long distance_um, long extrusionWidth_um, int brimCount)
		{
			bool hasWipeTower = storage.WipeTower.PolygonLength() > 0;

			Polygons skirtPolygons = new Polygons();

			if (config.EnableRaft)
			{
				skirtPolygons = skirtPolygons.CreateUnion(storage.raftOutline);
			}
			else
			{
				Polygons allOutlines = hasWipeTower ? new Polygons(storage.WipeTower.Offset(-extrusionWidth_um / 2)) : new Polygons();

				if (storage.WipeShield.Count > 0
					&& storage.WipeShield[0].Count > 0)
				{
					allOutlines = allOutlines.CreateUnion(storage.WipeShield[0].Offset(-extrusionWidth_um / 2));
				}

				// Loop over every extruder
				for (int extrudeIndex = 0; extrudeIndex < storage.Extruders.Count; extrudeIndex++)
				{
					// Only process the first extruder on spiral vase or
					// skip extruders that have empty layers
					if (config.ContinuousSpiralOuterPerimeter)
					{
						SliceLayer layer0 = storage.Extruders[extrudeIndex].Layers[0];
						allOutlines.AddAll(layer0.Islands[0]?.IslandOutline);
					}
					else
					{
						// Add the layers outline to allOutlines
						SliceLayer layer = storage.Extruders[extrudeIndex].Layers[0];
						foreach(var island in layer.Islands)
						{
							if (island.IslandOutline?.Count > 0)
							{
								allOutlines.Add(island.IslandOutline[0]);
							}
						}
					}
				}

				if (brimCount > 0)
				{
					Polygons brimIslandOutlines = new Polygons();

					// Grow each island by the current brim distance
					// Union the island brims
					brimIslandOutlines = brimIslandOutlines.CreateUnion(allOutlines);

					if (storage.Support != null)
					{
						brimIslandOutlines = brimIslandOutlines.CreateUnion(storage.Support.GetBedOutlines());
					}

					Polygons brimLoops = new Polygons();
					for (int brimIndex = 0; brimIndex < brimCount; brimIndex++)
					{
						// Extend the polygons to account for the brim (ensures convex hull takes this data into account)
						brimLoops.AddAll(brimIslandOutlines.Offset(extrusionWidth_um * brimIndex + extrusionWidth_um / 2));
					}

					storage.Brims.AddAll(brimLoops);

					// and extend the bounds of the skirt polygons
					skirtPolygons = skirtPolygons.CreateUnion(brimIslandOutlines.Offset(extrusionWidth_um * brimCount));
				}

				skirtPolygons = skirtPolygons.CreateUnion(allOutlines);

				if (storage.Support != null)
				{
					skirtPolygons = skirtPolygons.CreateUnion(storage.Support.GetBedOutlines());
				}
			}

			return skirtPolygons;
		}

		int lastLayerWithChange = -1;
		bool calculatedLastLayer = false;

		public int LastLayerWithChange(ConfigSettings config)
		{
			if (calculatedLastLayer)
			{
				return lastLayerWithChange;
			}

			int numLayers = Extruders[0].Layers.Count;
			int firstExtruderWithData = -1;
			for (int checkLayer = numLayers - 1; checkLayer >= 0; checkLayer--)
			{
				for (int extruderToCheck = 0; extruderToCheck < config.ExtruderCount; extruderToCheck++)
				{
					if ((extruderToCheck < Extruders.Count && Extruders[extruderToCheck].Layers[checkLayer].AllOutlines.Count > 0)
						|| (config.SupportExtruder == extruderToCheck && Support != null && Support.HasNormalSupport(checkLayer))
						|| (config.SupportInterfaceExtruder == extruderToCheck && Support != null && Support.HasInterfaceSupport(checkLayer)))
					{
						if (firstExtruderWithData == -1)
						{
							firstExtruderWithData = extruderToCheck;
						}
						else
						{
							if (firstExtruderWithData != extruderToCheck)
							{
								// have to remember the layer one above this so that we can switch back
								lastLayerWithChange = checkLayer + 1;
								calculatedLastLayer = true;
								return lastLayerWithChange;
							}
						}
					}
				}
			}

			calculatedLastLayer = true;
			return -1;
		}

		public bool NeedToPrintWipeTower(int layerIndex, ConfigSettings config)
		{
			if (HaveWipeTower(config))
			{
				return layerIndex <= LastLayerWithChange(config);
			}

			return false;
		}
	}
}