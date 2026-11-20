using UnityEngine;

public sealed class Repro : MonoBehaviour
{
    private void Start()
    {
        Debug.Log("REPRO reached Start");
        Application.Quit(0);
    }
}
