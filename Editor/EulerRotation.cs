using System.Numerics;

namespace Editor;

// Converts between a quaternion and yaw / pitch / roll in degrees, for the Inspector's Rotation field.
// Order: yaw about Y, then pitch about X, then roll about Z.
public static class EulerRotation
{
    public static Quaternion FromDegrees(float yaw, float pitch, float roll)
    {
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Rad(yaw));
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Rad(pitch));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Rad(roll));
        return Quaternion.Normalize(qy * qx * qz);
    }

    public static Vector3 ToDegrees(Quaternion q)
    {
        q = Quaternion.Normalize(q);

        // Rotation matrix elements for q (column-vector convention).
        float r02 = 2f * (q.X * q.Z + q.Y * q.W);
        float r12 = 2f * (q.Y * q.Z - q.X * q.W);
        float r10 = 2f * (q.X * q.Y + q.Z * q.W);
        float r11 = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        float r00 = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        float r22 = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float r20 = 2f * (q.X * q.Z - q.Y * q.W);

        float pitch = MathF.Asin(Math.Clamp(-r12, -1f, 1f));
        float yaw, roll;
        if (MathF.Abs(r12) < 0.9999f)
        {
            yaw = MathF.Atan2(r02, r22);
            roll = MathF.Atan2(r10, r11);
        }
        else
        {
            // Gimbal lock at pitch = +-90: yaw and roll turn about the same axis, so put it all in yaw.
            yaw = MathF.Atan2(-r20, r00);
            roll = 0f;
        }

        return new Vector3(Deg(yaw), Deg(pitch), Deg(roll));
    }

    private static float Rad(float degrees) => degrees * MathF.PI / 180f;
    private static float Deg(float radians) => radians * 180f / MathF.PI;
}
