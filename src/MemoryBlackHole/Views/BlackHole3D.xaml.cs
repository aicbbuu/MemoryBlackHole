using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MemoryBlackHole.Views
{
    /// <summary>
    /// 风格化真 3D 黑洞。
    /// 视觉结构参考 GPU 甜甜圈类演示：有厚度的 Torus 环面、明暗材质、
    /// 多层独立旋转和粒子。所有网格均在运行时生成，不使用第三方模型。
    /// </summary>
    public partial class BlackHole3D : UserControl
    {
        private AxisAngleRotation3D _wholeRotation = null!;
        private AxisAngleRotation3D _diskRotation = null!;
        private AxisAngleRotation3D _tiltRotation = null!;
        private EventHandler? _renderHandler;
        private double _wholeAngle;
        private double _diskAngle;
        private double _tiltAngle;

        public BlackHole3D()
        {
            InitializeComponent();

            holeModel.Geometry = BuildSphere(1.08, 40, 24);
            diskModel.Geometry = BuildTorus(2.05, 0.48, 128, 28);
            innerDiskModel.Geometry = BuildTorus(1.52, 0.18, 128, 16);
            outerDiskModel.Geometry = BuildTorus(2.55, 0.10, 128, 12);
            sparksGroup.Children.Add(BuildSparks(2.05, 18));

            _wholeRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            _diskRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            _tiltRotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);

            holeGroup.Transform = new RotateTransform3D(_wholeRotation);
            diskModel.Transform = new RotateTransform3D(_diskRotation);

            var tiltGroup = new Transform3DGroup();
            tiltGroup.Children.Add(new RotateTransform3D(_tiltRotation));
            tiltGroup.Children.Add(new RotateTransform3D(_diskRotation));
            innerDiskModel.Transform = tiltGroup;

            _renderHandler = (_, _) =>
            {
                _wholeAngle = (_wholeAngle + 0.34) % 360.0;
                _diskAngle = (_diskAngle - 0.92) % 360.0;
                _tiltAngle = (_tiltAngle + 0.18) % 360.0;
                _wholeRotation.Angle = _wholeAngle;
                _diskRotation.Angle = _diskAngle;
                _tiltRotation.Angle = 10.0 + Math.Sin(_tiltAngle * Math.PI / 180.0) * 7.0;
            };
            CompositionTarget.Rendering += _renderHandler;
            Unloaded += (_, _) => StopAnimation();
        }

        private void StopAnimation()
        {
            if (_renderHandler != null)
            {
                CompositionTarget.Rendering -= _renderHandler;
                _renderHandler = null;
            }
        }

        private static MeshGeometry3D BuildSphere(double radius, int slices, int stacks)
        {
            var mesh = new MeshGeometry3D();
            for (int stack = 0; stack < stacks; stack++)
            {
                double p0 = Math.PI * stack / stacks;
                double p1 = Math.PI * (stack + 1) / stacks;
                for (int slice = 0; slice < slices; slice++)
                {
                    double t0 = 2 * Math.PI * slice / slices;
                    double t1 = 2 * Math.PI * (slice + 1) / slices;
                    AddTriangle(mesh, SpherePoint(radius, p0, t0), SpherePoint(radius, p1, t0), SpherePoint(radius, p0, t1));
                    AddTriangle(mesh, SpherePoint(radius, p0, t1), SpherePoint(radius, p1, t0), SpherePoint(radius, p1, t1));
                }
            }
            mesh.Freeze();
            return mesh;
        }

        private static Point3D SpherePoint(double r, double phi, double theta)
        {
            return new Point3D(
                r * Math.Sin(phi) * Math.Cos(theta),
                r * Math.Cos(phi),
                r * Math.Sin(phi) * Math.Sin(theta));
        }

        private static MeshGeometry3D BuildTorus(double majorRadius, double tubeRadius, int segments, int tubeSegments)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < segments; i++)
            {
                double u0 = 2 * Math.PI * i / segments;
                double u1 = 2 * Math.PI * (i + 1) / segments;
                for (int j = 0; j < tubeSegments; j++)
                {
                    double v0 = 2 * Math.PI * j / tubeSegments;
                    double v1 = 2 * Math.PI * (j + 1) / tubeSegments;

                    AddTriangle(mesh, TorusPoint(majorRadius, tubeRadius, u0, v0),
                        TorusPoint(majorRadius, tubeRadius, u1, v0),
                        TorusPoint(majorRadius, tubeRadius, u0, v1));
                    AddTriangle(mesh, TorusPoint(majorRadius, tubeRadius, u0, v1),
                        TorusPoint(majorRadius, tubeRadius, u1, v0),
                        TorusPoint(majorRadius, tubeRadius, u1, v1));
                }
            }
            mesh.Freeze();
            return mesh;
        }

        private static Point3D TorusPoint(double major, double tube, double u, double v)
        {
            double ring = major + tube * Math.Cos(v);
            // XZ 平面上的厚环面：从斜向相机看有明显深度
            return new Point3D(ring * Math.Cos(u), tube * Math.Sin(v), ring * Math.Sin(u));
        }

        private static void AddTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
        {
            int index = mesh.Positions.Count;
            mesh.Positions.Add(a);
            mesh.Positions.Add(b);
            mesh.Positions.Add(c);

            Vector3D normal = Vector3D.CrossProduct(b - a, c - a);
            normal.Normalize();
            mesh.Normals.Add(normal);
            mesh.Normals.Add(normal);
            mesh.Normals.Add(normal);
            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 2);
        }

        private static Model3DGroup BuildSparks(double radius, int count)
        {
            var group = new Model3DGroup();
            for (int i = 0; i < count; i++)
            {
                double angle = 2 * Math.PI * i / count;
                double r = radius + (i % 3 - 1) * 0.15;
                var spark = new GeometryModel3D
                {
                    Geometry = BuildSphere(0.045 + (i % 4) * 0.012, 10, 6),
                    Material = new EmissiveMaterial(new SolidColorBrush(
                        i % 3 == 0 ? Color.FromRgb(0xFF, 0xB5, 0x2E) :
                        i % 3 == 1 ? Color.FromRgb(0xFF, 0x5A, 0x12) :
                        Color.FromRgb(0x58, 0xC7, 0xFF)))
                };
                spark.Transform = new TranslateTransform3D(
                    Math.Cos(angle) * r, 0.12 + Math.Sin(angle * 3) * 0.08, Math.Sin(angle) * r);
                group.Children.Add(spark);
            }
            return group;
        }
    }
}
