using System.Numerics;

namespace AlphaChannel.Plugin.Video;

// Publish all placement fields together. A render pass must never combine the
// position from one update with the yaw/size from another update.
internal sealed class ScreenRenderTransform
{
    internal sealed record Snapshot(Vector3 Position, float Yaw, float WidthScale, float HeightScale)
    {
        internal Matrix4x4 ProjectBackdrop(Matrix4x4 viewProjection, float extraScale)
        {
            var behind = Vector3.Transform(-Vector3.UnitZ * .15f * WidthScale, Matrix4x4.CreateRotationY(Yaw));
            return (this with { Position = Position + behind }).Project(viewProjection, extraScale);
        }

        internal Matrix4x4 ProjectFloor(Matrix4x4 viewProjection, float height)
        {
            var world = Matrix4x4.CreateScale(WidthScale * 2.2f, WidthScale * 1.6f, 1) *
                        Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateRotationY(Yaw) *
                        Matrix4x4.CreateTranslation(new Vector3(Position.X, height, Position.Z));
            return world * viewProjection;
        }

        internal Matrix4x4 Project(Matrix4x4 viewProjection, float extraScale = 1f)
        {
            var width = WidthScale * extraScale;
            var height = HeightScale * extraScale;
            var world = Matrix4x4.CreateScale(width, 0.6f * height, (width + height) * 0.5f) *
                        Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, Yaw) *
                        Matrix4x4.CreateTranslation(Position);
            return world * viewProjection;
        }
    }

    private Snapshot current = new(Vector3.Zero, 0f, 1f, 1f);
    internal Snapshot Read() => Volatile.Read(ref current);

    internal void Update(Vector3 position, float yaw, float widthScale, float heightScale)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            !float.IsFinite(yaw) || !float.IsFinite(widthScale) || !float.IsFinite(heightScale) ||
            widthScale <= 0 || heightScale <= 0) return;
        var previous = Read();
        if (previous.Position == position && previous.Yaw == yaw &&
            previous.WidthScale == widthScale && previous.HeightScale == heightScale) return;
        Volatile.Write(ref current, new(position, yaw, widthScale, heightScale));
    }

    internal static bool IsValidCameraMatrix(Matrix4x4 matrix) => matrix != default &&
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    internal static Matrix4x4? BuildCameraViewProjection(Vector3 position, Vector3 lookAt,
        float fov, float aspect, float near, float far)
    {
        if (!float.IsFinite(fov) || fov <= 0 || fov >= MathF.PI ||
            !float.IsFinite(aspect) || aspect <= 0 || !float.IsFinite(near) || near <= 0 ||
            !float.IsFinite(far) || far <= near) return null;
        var view = Matrix4x4.CreateLookAt(position, lookAt, Vector3.UnitY);
        var yScale = 1f / MathF.Tan(fov / 2f);
        var projection = new Matrix4x4(
            yScale / aspect, 0, 0, 0,
            0, yScale, 0, 0,
            0, 0, near / (far - near), -1,
            0, 0, near * far / (far - near), 0);
        return IsValidCameraMatrix(view) && IsValidCameraMatrix(projection) ? view * projection : null;
    }
}
