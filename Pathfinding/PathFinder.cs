/*
Copyright (c) 2015, Lars Brubaker
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System.Collections.Generic;
using MSClipperLib;

namespace MatterHackers.Pathfinding
{
	using System;
	using System.IO;
	using MatterHackers.Agg;
	using MatterHackers.Agg.Image;
	using MatterHackers.Agg.Transform;
	using MatterHackers.Agg.VertexSource;
	using QuadTree;
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;

	public class PathFinder
	{
		public static Action<PathFinder, Polygon, IntPoint, IntPoint> CalculatedPath = null;
		private static string lastOutlineString = "";
		private static bool saveBadPathToDisk = false;
		private PathingData boundryData;
		private bool useOutlineAsBoundry = false;

		public PathFinder(Polygons inOutlinePolygons, long avoidInset, IntRect? stayInsideBounds = null, bool useIsInsideCache = true)
		{
			if (inOutlinePolygons.Count == 0)
			{
				return;
			}

			InsetAmount = avoidInset;

			var outlinePolygons = FixWinding(inOutlinePolygons);
			outlinePolygons = Clipper.CleanPolygons(outlinePolygons, InsetAmount / 60);
			if (stayInsideBounds != null)
			{
				var boundary = stayInsideBounds.Value;
				outlinePolygons.Add(new Polygon()
				{
					new IntPoint(boundary.minX, boundary.minY),
					new IntPoint(boundary.maxX, boundary.minY),
					new IntPoint(boundary.maxX, boundary.maxY),
					new IntPoint(boundary.minX, boundary.maxY),
				});

				outlinePolygons = FixWinding(outlinePolygons);
			}

			var boundaryPolygons = outlinePolygons.Offset(stayInsideBounds == null ? -InsetAmount : -2 * InsetAmount);
			boundaryPolygons = FixWinding(boundaryPolygons);

			// set it to 1/4 the inset amount
			int devisor = 4;
			boundryData = new PathingData(boundaryPolygons, avoidInset / devisor, useIsInsideCache);
			OutlineData = new PathingData(outlinePolygons, avoidInset / devisor, useIsInsideCache);
		}

		public long InsetAmount { get; private set; }
		public PathingData OutlineData { get; }

		public PathingData PathingData
		{
			get
			{
				if (useOutlineAsBoundry)
				{
					return OutlineData;
				}

				return boundryData;
			}
		}

		private long findNodeDist { get { return InsetAmount / 100; } }

		private WayPointsToRemove RemoveBoundryPointList
		{
			get
			{
				if (useOutlineAsBoundry)
				{
					return OutlineData.RemovePointList;
				}

				return boundryData.RemovePointList;
			}
		}

		public bool AllPathSegmentsAreInsideOutlines(Polygon pathThatIsInside, IntPoint startPoint, IntPoint endPoint, bool writeErrors = false, int layerIndex = -1)
		{
			PathingData outlineData = PathingData;
			if(outlineData.Polygons.Count > 1)
			{
				outlineData = this.OutlineData;
			}
			//if (outlineData.Polygons.Count > 1) throw new Exception();
			// check that this path does not exit the outline
			for (int i = 0; i < pathThatIsInside.Count - 1; i++)
			{
				var start = pathThatIsInside[i];
				var end = pathThatIsInside[i + 1];

				if (start != startPoint
					&& start != endPoint
					&& end != endPoint
					&& end != startPoint)
				{
					if (!ValidPoint(outlineData, start + (end - start) / 4)
						|| !ValidPoint(outlineData, start + (end - start) / 2)
						|| !ValidPoint(outlineData, start + (end - start) * 3 / 4)
						|| !ValidPoint(outlineData, start + (end - start) / 10)
						|| !ValidPoint(outlineData, start + (end - start) * 9 / 10)
						|| (start - end).Length() > 1000000)
					{
						// an easy way to get the path
						if (writeErrors)
						{
							WriteErrorForTesting(layerIndex, startPoint, endPoint, (end - start).Length());
						}

						return false;
					}
				}
			}

			return true;
		}

		public bool CreatePathInsideBoundary(IntPoint startPointIn, IntPoint endPointIn, Polygon pathThatIsInside, bool optimizePath = true, int layerIndex = -1)
		{
			var goodPath = CreatePathInsideBoundaryInternal(startPointIn, endPointIn, pathThatIsInside, layerIndex);

			// This is good to disable when improving pathing
			bool agressivePathSolution = true;
			if (agressivePathSolution && !goodPath)
			{
				// could not find a path in the Boundry find one in the outline
				useOutlineAsBoundry = true;
				goodPath = CreatePathInsideBoundaryInternal(startPointIn, endPointIn, pathThatIsInside, layerIndex);
				useOutlineAsBoundry = false;

				if (goodPath)
				{
					MovePointsInsideIfPossible(startPointIn, endPointIn, pathThatIsInside);
				}
			}

			// remove any segment that goes to one point and then back to same point (a -> b -> a)
			RemoveUTurnSegments(startPointIn, endPointIn, pathThatIsInside);

			if (optimizePath)
			{
				OptimizePathPoints(pathThatIsInside);
			}

			if (saveBadPathToDisk)
			{
				AllPathSegmentsAreInsideOutlines(pathThatIsInside, startPointIn, endPointIn, true, layerIndex);
			}

			CalculatedPath?.Invoke(this, pathThatIsInside, startPointIn, endPointIn);

			return goodPath;
		}

		private static void RemoveUTurnSegments(IntPoint startPointIn, IntPoint endPointIn, Polygon pathThatIsInside)
		{
			if (pathThatIsInside.Count > 1)
			{
				IntPoint startPoint = startPointIn;
				for (int i = 0; i < pathThatIsInside.Count - 1; i++)
				{
					IntPoint testPoint = pathThatIsInside[i];
					IntPoint endPoint = i < pathThatIsInside.Count - 2 ? pathThatIsInside[i + 1] : endPointIn;

					if (endPoint == startPoint)
					{
						pathThatIsInside.RemoveAt(i);
						i--;
					}

					startPoint = testPoint;
				}
			}
		}

		private void MovePointsInsideIfPossible(IntPoint startPointIn, IntPoint endPointIn, Polygon pathThatIsInside)
		{
			// move every segment that can be inside the boundry to be within the boundry
			if (pathThatIsInside.Count > 1)
			{
				IntPoint startPoint = startPointIn;
				for (int i = 0; i < pathThatIsInside.Count - 1; i++)
				{
					IntPoint testPoint = pathThatIsInside[i];
					IntPoint endPoint = i < pathThatIsInside.Count - 2 ? pathThatIsInside[i + 1] : endPointIn;

					IntPoint inPolyPosition;
					if (MovePointInsideBoundary(testPoint, out inPolyPosition))
					{
						useOutlineAsBoundry = true;
						// It moved so test if it is a good point
						if (PathingData.Polygons.FindIntersection(startPoint, inPolyPosition, PathingData.EdgeQuadTrees) != Intersection.Intersect
							&& PathingData.Polygons.FindIntersection(inPolyPosition, endPoint, PathingData.EdgeQuadTrees) != Intersection.Intersect)
						{
							testPoint = inPolyPosition;
							pathThatIsInside[i] = testPoint;
						}

						useOutlineAsBoundry = false;
					}

					startPoint = testPoint;
				}
			}
		}

		public bool MovePointInsideBoundary(IntPoint testPosition, out IntPoint inPolyPosition)
		{
			inPolyPosition = testPosition;
			if (!PointIsInsideBoundary(testPosition))
			{
				Tuple<int, int, IntPoint> endPolyPointPosition = null;
				PathingData.Polygons.MovePointInsideBoundary(testPosition, out endPolyPointPosition,
					PathingData.EdgeQuadTrees,
					PathingData.PointQuadTrees,
					PathingData.PointIsInside);

				if (endPolyPointPosition != null)
				{
					inPolyPosition = endPolyPointPosition.Item3;
					return true;
				}
			}

			return false;
		}

		public bool PointIsInsideBoundary(IntPoint intPoint)
		{
			return PathingData.PointIsInside(intPoint) == QTPolygonsExtensions.InsideState.Inside;
		}

		private IntPointNode AddTempWayPoint(WayPointsToRemove removePointList, IntPoint position)
		{
			var node = PathingData.Waypoints.AddNode(position);
			removePointList.Add(node);
			return node;
		}

		private bool CreatePathInsideBoundaryInternal(IntPoint startPointIn, IntPoint endPointIn, Polygon pathThatIsInside, int layerIndex)
		{
			double z = startPointIn.Z;
			startPointIn.Z = 0;
			endPointIn.Z = 0;
			if (PathingData?.Polygons == null
				|| PathingData?.Polygons.Count == 0)
			{
				return false;
			}

			// neither needed to be moved
			if (PathingData.Polygons.FindIntersection(startPointIn, endPointIn, PathingData.EdgeQuadTrees) == Intersection.None
				&& PathingData.PointIsInside((startPointIn + endPointIn) / 2) == QTPolygonsExtensions.InsideState.Inside)
			{
				return true;
			}

			RemoveBoundryPointList.Dispose();

			pathThatIsInside.Clear();

			//Check if we are inside the boundaries
			IntPointNode startPlanNode = null;
			var lastAddedNode = GetWayPointInside(startPointIn, out startPlanNode);

			IntPointNode endPlanNode = null;
			var lastToAddNode = GetWayPointInside(endPointIn, out endPlanNode);

			long startToEndDistanceSqrd = (endPointIn - startPointIn).LengthSquared();
			long moveStartInDistanceSqrd = (startPlanNode.Position - lastAddedNode.Position).LengthSquared();
			long moveEndInDistanceSqrd = (endPlanNode.Position - lastToAddNode.Position).LengthSquared();
			// if we move both points less than the distance of this segment
			if (startToEndDistanceSqrd < moveStartInDistanceSqrd
				&& startToEndDistanceSqrd < moveEndInDistanceSqrd)
			{
				// then go ahead and say it is a good path
				return true;
			}

			var crossings = new List<Tuple<int, int, IntPoint>>(PathingData.Polygons.FindCrossingPoints(lastAddedNode.Position, lastToAddNode.Position, PathingData.EdgeQuadTrees));
			if(crossings.Count == 0)
			{
				return true;
			}
			crossings.Sort(new PolygonAndPointDirectionSorter(lastAddedNode.Position, lastToAddNode.Position));
			foreach (var crossing in crossings.SkipSame())
			{
				IntPointNode crossingNode = PathingData.Waypoints.FindNode(crossing.Item3, findNodeDist);
				// for every crossing try to connect it up in the waypoint data
				if (crossingNode == null)
				{
					crossingNode = AddTempWayPoint(RemoveBoundryPointList, crossing.Item3);
					// also connect it to the next and prev points on the polygon it came from
					HookUpToEdge(crossingNode, crossing.Item1, crossing.Item2);
				}

				if (lastAddedNode != crossingNode
					&& (PathingData.PointIsInside((lastAddedNode.Position + crossingNode.Position) / 2) == QTPolygonsExtensions.InsideState.Inside
						|| lastAddedNode.Links.Count == 0))
				{
					PathingData.Waypoints.AddPathLink(lastAddedNode, crossingNode);
				}
				else if (crossingNode.Links.Count == 0)
				{
					// link it to the edge it is on
					HookUpToEdge(crossingNode, crossing.Item1, crossing.Item2);
				}
				lastAddedNode = crossingNode;
			}

			if (lastAddedNode != lastToAddNode
				&& (PathingData.PointIsInside((lastAddedNode.Position + lastToAddNode.Position) / 2) == QTPolygonsExtensions.InsideState.Inside
					|| lastToAddNode.Links.Count == 0))
			{
				// connect the last crossing to the end node
				PathingData.Waypoints.AddPathLink(lastAddedNode, lastToAddNode);
			}

			Path<IntPointNode> path = PathingData.Waypoints.FindPath(startPlanNode, endPlanNode, true);

			foreach (var node in path.Nodes.SkipSamePosition())
			{
				pathThatIsInside.Add(new IntPoint(node.Position, z));
			}

			if (path.Nodes.Length == 0)
			{
				if (saveBadPathToDisk)
				{
					WriteErrorForTesting(layerIndex, startPointIn, endPointIn, 0);
				}
				return false;
			}

			return true;
		}

		private Polygons FixWinding(Polygons polygonsToPathAround)
		{
			polygonsToPathAround = Clipper.CleanPolygons(polygonsToPathAround);
			Polygon boundsPolygon = new Polygon();
			IntRect bounds = Clipper.GetBounds(polygonsToPathAround);
			bounds.minX -= 10;
			bounds.maxY += 10;
			bounds.maxX += 10;
			bounds.minY -= 10;

			boundsPolygon.Add(new IntPoint(bounds.minX, bounds.minY));
			boundsPolygon.Add(new IntPoint(bounds.maxX, bounds.minY));
			boundsPolygon.Add(new IntPoint(bounds.maxX, bounds.maxY));
			boundsPolygon.Add(new IntPoint(bounds.minX, bounds.maxY));

			Clipper clipper = new Clipper();

			clipper.AddPaths(polygonsToPathAround, PolyType.ptSubject, true);
			clipper.AddPath(boundsPolygon, PolyType.ptClip, true);

			PolyTree intersectionResult = new PolyTree();
			clipper.Execute(ClipType.ctIntersection, intersectionResult);

			Polygons outputPolygons = Clipper.ClosedPathsFromPolyTree(intersectionResult);

			return outputPolygons;
		}

		private IntPointNode GetWayPointInside(IntPoint position, out IntPointNode waypointAtPosition)
		{
			Tuple<int, int, IntPoint> foundPolyPointPosition;
			waypointAtPosition = null;
			PathingData.Polygons.MovePointInsideBoundary(position, out foundPolyPointPosition, PathingData.EdgeQuadTrees, PathingData.PointQuadTrees, PathingData.PointIsInside);
			if (foundPolyPointPosition == null)
			{
				// The point is already inside
				var existingNode = PathingData.Waypoints.FindNode(position, findNodeDist);
				if (existingNode == null)
				{
					waypointAtPosition = AddTempWayPoint(RemoveBoundryPointList, position);
					return waypointAtPosition;
				}
				waypointAtPosition = existingNode;
				return waypointAtPosition;
			}
			else // The point had to be moved inside the polygon
			{
				if (position == foundPolyPointPosition.Item3)
				{
					var existingNode = PathingData.Waypoints.FindNode(position, findNodeDist);
					if (existingNode != null)
					{
						waypointAtPosition = existingNode;
						return waypointAtPosition;
					}
					else
					{
						// get the way point that we need to insert
						waypointAtPosition = AddTempWayPoint(RemoveBoundryPointList, position);
						HookUpToEdge(waypointAtPosition, foundPolyPointPosition.Item1, foundPolyPointPosition.Item2);
						return waypointAtPosition;
					}
				}
				else // the point was outside, hook it up to the nearest edge
				{
					// find the start node if we can
					IntPointNode startNode = PathingData.Waypoints.FindNode(foundPolyPointPosition.Item3, findNodeDist);

					// After that create a temp way point at the current position
					waypointAtPosition = AddTempWayPoint(RemoveBoundryPointList, position);
					if (startNode != null)
					{
						PathingData.Waypoints.AddPathLink(startNode, waypointAtPosition);
					}
					else
					{
						// get the way point that we need to insert
						startNode = AddTempWayPoint(RemoveBoundryPointList, foundPolyPointPosition.Item3);
						HookUpToEdge(startNode, foundPolyPointPosition.Item1, foundPolyPointPosition.Item2);
						PathingData.Waypoints.AddPathLink(startNode, waypointAtPosition);
					}
					return startNode;
				}
			}
		}

		private void HookUpToEdge(IntPointNode crossingNode, int polyIndex, int pointIndex)
		{
			int count = PathingData.Polygons[polyIndex].Count;
			if (count > 0)
			{
				pointIndex = (pointIndex + count) % count;
				IntPointNode prevPolyPointNode = PathingData.Waypoints.FindNode(PathingData.Polygons[polyIndex][pointIndex]);
				PathingData.Waypoints.AddPathLink(crossingNode, prevPolyPointNode);
				IntPointNode nextPolyPointNode = PathingData.Waypoints.FindNode(PathingData.Polygons[polyIndex][(pointIndex + 1) % count]);
				PathingData.Waypoints.AddPathLink(crossingNode, nextPolyPointNode);
			}
		}

		private void OptimizePathPoints(Polygon pathThatIsInside)
		{
			for (int startIndex = 0; startIndex < pathThatIsInside.Count - 2; startIndex++)
			{
				var startPosition = pathThatIsInside[startIndex];
				if (startPosition.X < -10000)
				{
					int a = 0;
				}
				for (int endIndex = pathThatIsInside.Count - 1; endIndex > startIndex + 1; endIndex--)
				{
					var endPosition = pathThatIsInside[endIndex];

					var crossings = new List<Tuple<int, int, IntPoint>>(PathingData.Polygons.FindCrossingPoints(startPosition, endPosition, PathingData.EdgeQuadTrees));

					bool isCrossingEdge = false;
					foreach (var cross in crossings)
					{
						if (cross.Item3 != startPosition
							&& cross.Item3 != endPosition)
						{
							isCrossingEdge = true;
							break;
						}
					}

					if (!isCrossingEdge
						&& PathingData.PointIsInside((startPosition + endPosition) / 2) == QTPolygonsExtensions.InsideState.Inside)
					{
						// remove A+1 - B-1
						for (int removeIndex = endIndex - 1; removeIndex > startIndex; removeIndex--)
						{
							pathThatIsInside.RemoveAt(removeIndex);
						}

						endIndex = pathThatIsInside.Count - 1;
					}
				}
			}
		}

		private bool ValidPoint(PathingData outlineData, IntPoint position)
		{
			Tuple<int, int, IntPoint> movedPosition;
			long movedDist = 0;
			PathingData.Polygons.MovePointInsideBoundary(position, out movedPosition, PathingData.EdgeQuadTrees, PathingData.PointQuadTrees, PathingData.PointIsInside);
			if (movedPosition != null)
			{
				movedDist = (position - movedPosition.Item3).Length();
			}

			if (outlineData.Polygons.TouchingEdge(position, outlineData.EdgeQuadTrees)
			|| outlineData.PointIsInside(position) != QTPolygonsExtensions.InsideState.Outside
			|| movedDist <= 1)
			{
				return true;
			}

			return false;
		}

		private void WriteErrorForTesting(int layerIndex, IntPoint startPoint, IntPoint endPoint, long edgeLength)
		{
			var bounds = OutlineData.Polygons.GetBounds();
			long length = (startPoint - endPoint).Length();
			string startEndString = $"start:({startPoint.X}, {startPoint.Y}), end:({endPoint.X}, {endPoint.Y})";
			string outlineString = OutlineData.Polygons.WriteToString();
			// just some code to set a break point on
			string fullPath = Path.GetFullPath("DebugPathFinder.txt");
			if (fullPath.Contains("MatterControl"))
			{
				using (StreamWriter sw = File.AppendText(fullPath))
				{
					if (lastOutlineString != outlineString)
					{
						sw.WriteLine("");
						sw.WriteLine($"polyPath = \"{outlineString}\";");
						lastOutlineString = outlineString;
					}
					sw.WriteLine($"// layerIndex = {layerIndex}");
					sw.WriteLine($"// Length of this segment (start->end) {length}. Length of bad edge {edgeLength}");
					sw.WriteLine($"// startOverride = new MSIntPoint({startPoint.X}, {startPoint.Y}); endOverride = new MSIntPoint({endPoint.X}, {endPoint.Y});");
					sw.WriteLine($"TestSinglePathIsInside(polyPath, new IntPoint({startPoint.X}, {startPoint.Y}), new IntPoint({endPoint.X}, {endPoint.Y}));");
				}
			}
		}
	}

	/// <summary>
	/// This is to hold all the data that lets us switch between Boundry and Outline pathing.
	/// </summary>
	public class PathingData
	{
		private Affine polygonsToImageTransform;
		private double unitsPerPixel;
		private bool usingPathingCache;

		internal PathingData(Polygons polygons, double unitsPerPixel, bool usingPathingCache)
		{
			this.usingPathingCache = usingPathingCache;
			this.unitsPerPixel = Math.Max(1, unitsPerPixel);

			Polygons = polygons;
			EdgeQuadTrees = Polygons.GetEdgeQuadTrees();
			PointQuadTrees = Polygons.GetPointQuadTrees();

			foreach (var polygon in Polygons)
			{
				Waypoints.AddPolygon(polygon);
			}

			RemovePointList = new WayPointsToRemove(Waypoints);

			GenerateIsideCache();
		}

		public List<QuadTree<int>> EdgeQuadTrees { get; }
		public ImageBuffer InsideCache { get; private set; }
		public List<QuadTree<int>> PointQuadTrees { get; }
		public Polygons Polygons { get; }
		public WayPointsToRemove RemovePointList { get; }
		public IntPointPathNetwork Waypoints { get; } = new IntPointPathNetwork();

		public static PathStorage CreatePathStorage(List<List<IntPoint>> polygons)
		{
			PathStorage output = new PathStorage();

			foreach (List<IntPoint> polygon in polygons)
			{
				bool first = true;
				foreach (IntPoint point in polygon)
				{
					if (first)
					{
						output.Add(point.X, point.Y, ShapePath.FlagsAndCommand.CommandMoveTo);
						first = false;
					}
					else
					{
						output.Add(point.X, point.Y, ShapePath.FlagsAndCommand.CommandLineTo);
					}
				}

				output.ClosePolygon();
			}
			return output;
		}

		public QTPolygonsExtensions.InsideState PointIsInside(IntPoint testPoint)
		{
			if (!usingPathingCache)
			{
				if (Polygons.PointIsInside(testPoint, EdgeQuadTrees, PointQuadTrees))
				{
					return QTPolygonsExtensions.InsideState.Inside;
				}

				return QTPolygonsExtensions.InsideState.Outside;
			}

			// translate the test point to the image coordinates
			double x = testPoint.X;
			double y = testPoint.Y;
			polygonsToImageTransform.transform(ref x, ref y);
			int xi = (int)(x + .5);
			int yi = (int)(y + .5);

			if (xi >= 0 && xi < InsideCache.Width
				&& yi >= 0 && yi < InsideCache.Height)
			{
				var valueAtPoint = InsideCache.GetPixel(xi, yi);
				if (valueAtPoint.red == 255)
				{
					return QTPolygonsExtensions.InsideState.Inside;
				}
				if (valueAtPoint.red == 0)
				{
					return QTPolygonsExtensions.InsideState.Outside;
				}

				return QTPolygonsExtensions.InsideState.Unknown;
			}

			return QTPolygonsExtensions.InsideState.Outside;
		}

		private void GenerateIsideCache()
		{
			var bounds = Polygons.GetBounds();
			var width = Math.Max(32, Math.Min(1024, (int)(bounds.Width() / unitsPerPixel + .5)));
			var height = Math.Max(32, Math.Min(1024, (int)(bounds.Height() / unitsPerPixel + .5)));

			InsideCache = new ImageBuffer(width + 4, height + 4, 8, new blender_gray(1));

			polygonsToImageTransform = Affine.NewIdentity();
			// move it to 0, 0
			polygonsToImageTransform *= Affine.NewTranslation(-bounds.minX, -bounds.minY);
			// scale to fit cache
			polygonsToImageTransform *= Affine.NewScaling(width / (double)bounds.Width(), height / (double)bounds.Height());
			// and move it in 2 pixels
			polygonsToImageTransform *= Affine.NewTranslation(2, 2);

			InsideCache.NewGraphics2D().Render(new VertexSourceApplyTransform(CreatePathStorage(Polygons), polygonsToImageTransform), RGBA_Bytes.White);
		}
	}
}