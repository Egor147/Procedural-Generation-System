using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class SimplePlayerController : MonoBehaviour
{
    public float Speed = 5f;
    public float RotationSpeed = 100f;

    void Update()
    {
        // WASD Movement
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        Vector3 move = transform.right * h + transform.forward * v;
        GetComponent<CharacterController>().Move(move * Speed * Time.deltaTime);

        // Mouse Rotation
        float rot = Input.GetAxis("Mouse X");
        transform.Rotate(0, rot * RotationSpeed * Time.deltaTime, 0);
    }
}