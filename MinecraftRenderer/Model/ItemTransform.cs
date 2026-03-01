namespace MinecraftRenderer.Model;

using System;
using System.Numerics;

public readonly record struct ItemTransform(Vector3 Rotation, Vector3 Translation, Vector3 Scale)
{
	public static readonly ItemTransform NoTransform = new(Vector3.Zero, Vector3.Zero, Vector3.One);

	public Matrix4x4 BuildMatrix(bool isLeftHand = false) {
		if (this == NoTransform) {
			return Matrix4x4.Identity;
		}

		var translationX = isLeftHand ? -Translation.X : Translation.X;
		var rotationY = isLeftHand ? -Rotation.Y : Rotation.Y;
		var rotationZ = isLeftHand ? -Rotation.Z : Rotation.Z;
		
		var translationMatrix =
			Matrix4x4.CreateTranslation(translationX / 16f, Translation.Y / 16f, Translation.Z / 16f);

		var rotationMatrix = Matrix4x4.CreateRotationZ(rotationZ * (MathF.PI / 180f))
		                     * Matrix4x4.CreateRotationY(rotationY * (MathF.PI / 180f))
		                     * Matrix4x4.CreateRotationX(Rotation.X * (MathF.PI / 180f));

		var scaleMatrix = Matrix4x4.CreateScale(Scale);
		
		return scaleMatrix * rotationMatrix * translationMatrix;
	}
}