namespace Scalpel.Tests.Models;

/// <summary>The kidney dish's open basin, fitted grips and rising iodine pool.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_kidney_dish"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class KidneyDishAppearanceTest {
    private const uint DishTestLayer = 1 << 18;

    [TestCase]
    public async Task OpenBasinClearsBothGlovesAndTheLiquidStaysInsideTheDish() {
        var studio = new ModelStudio(0.27f);
        try {
            var def = Db.Tool("kidney_dish")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                ((MeshInstance3D)model.FindChild("Pool", true, false)).Visible = false;
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the kidney dish clears glove {index}").IsEqual(0);
                await studio.Capture(model, "kidney_dish_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the kidney dish without clipping; its basin remains exposed",
                    new Vector3(0f, 0f, -0.10f), new Vector3(0.25f, 0.28f, 0.22f));
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }

            var tool = new SurgicalTool();
            studio.Root.AddChild(tool);
            tool.Setup(9000, def);
            tool.Freeze = true;
            var dish = (MeshInstance3D)tool.FindChild("Dish", true, false);
            var pool = (MeshInstance3D)tool.FindChild("Pool", true, false);
            tool.Ml = 0f;
            tool.ShowLiquid();
            AssertBool(pool.Visible).OverrideFailureMessage("the kidney dish's pool is hidden while empty").IsFalse();
            await studio.Capture(tool, "kidney_dish_model", "00_rest_oblique",
                "the actual kidney dish tool shows its empty, open basin", new Vector3(0f, 0f, -0.10f),
                new Vector3(0.25f, 0.28f, 0.22f));
            await studio.Capture(tool, "kidney_dish_model", "01_empty_top",
                "the empty basin has a continuous rim and a clearly shaped kidney outline",
                new Vector3(0f, 0f, -0.10f), new Vector3(0.001f, 0.4f, 0.001f));

            var dishPoints = Vertices(tool, dish).ToArray();
            var rimHeight = dishPoints.Max(vertex => vertex.Y);
            var dishFloor = dishPoints.Min(vertex => vertex.Y);
            var dishShape = new ConcavePolygonShape3D { BackfaceCollision = true };
            dishShape.SetFaces(dishPoints);
            var dishCollider = new StaticBody3D { CollisionLayer = DishTestLayer, CollisionMask = 0 };
            tool.AddChild(dishCollider);
            dishCollider.AddChild(new CollisionShape3D { Shape = dishShape });
            await Frames.NextPhysics();
            var space = tool.GetWorld3D().DirectSpaceState;
            var poolRestPosition = pool.Position;
            foreach (var fill in new[] { 0.01f, 0.25f, 0.5f, 0.65f, 1f }) {
                tool.Ml = def.Volume * fill;
                tool.Iodine = 1f;
                tool.ShowLiquid();
                AssertBool(pool.Visible)
                    .OverrideFailureMessage($"the kidney dish shows its liquid at {fill:P0} full").IsTrue();
                var liquidPoints = Vertices(tool, pool).ToArray();
                var liquidTop = liquidPoints.Max(vertex => vertex.Y);
                AssertFloat(liquidTop)
                    .OverrideFailureMessage($"the liquid surface at {fill:P0} stays below the basin rim")
                    .IsLess(rimHeight);
                var topVertices = liquidPoints
                    .Where(vertex => Mathf.Abs(vertex.Y - liquidTop) < 0.00001f)
                    .Select(vertex => new Vector2(vertex.X, vertex.Z))
                    .Distinct()
                    .ToArray();
                var escaped = new List<string>();
                foreach (var point in topVertices) {
                    var from = tool.ToGlobal(new Vector3(point.X, rimHeight + 0.01f, point.Y));
                    var to = tool.ToGlobal(new Vector3(point.X, dishFloor - 0.01f, point.Y));
                    var query = PhysicsRayQueryParameters3D.Create(from, to, DishTestLayer);
                    query.HitBackFaces = true;
                    var hit = space.IntersectRay(query);
                    if (hit.Count == 0) {
                        escaped.Add($"{point}: no basin below the pool");
                    } else {
                        var height = tool.ToLocal(hit["position"].AsVector3()).Y;
                        if (height > liquidTop + 0.0001f) {
                            escaped.Add($"{point}: basin at {height:0.00000}, liquid at {liquidTop:0.00000}");
                        }
                    }
                }
                AssertBool(topVertices.Length > 0 && escaped.Count == 0)
                    .OverrideFailureMessage($"the liquid surface at {fill:P0} stays within the dish cavity: {string.Join("; ", escaped.Take(3))}")
                    .IsTrue();
                AssertBool(pool.Position.IsEqualApprox(poolRestPosition))
                    .OverrideFailureMessage("filling the dish does not move the pool away from its floor origin")
                    .IsTrue();
            }
            tool.Ml = def.Volume * 0.65f;
            tool.Iodine = 1f;
            tool.ShowLiquid();
            await studio.Capture(tool, "kidney_dish_model", "04_tool_65ml_iodine",
                "65 ml of iodine forms a pool inside the kidney dish's rim", new Vector3(0f, 0f, -0.10f),
                new Vector3(0.25f, 0.28f, 0.22f));
            tool.QueueFree();
            await Frames.NextPhysics();
        } finally {
            await studio.Finish();
        }
    }

    private static IEnumerable<Vector3> Vertices(Node3D model, MeshInstance3D part) =>
        part.Mesh.GetFaces().Select(vertex => model.ToLocal(part.ToGlobal(vertex)));
}
