using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

public sealed class ArduinoSpeakerSequencer : MonoBehaviour
{
    [Serializable]
    private sealed class SpeakerGroup
    {
        public string name;
        public int[] speakerNumbers;
    }

    [Header("Serial")]
    [SerializeField] private string portName = "COM3";
    [SerializeField] private int baudRate = 115200;
    [SerializeField] private int readTimeoutMs = 50;
    [SerializeField] private int writeTimeoutMs = 100;
    [SerializeField] private bool connectOnStart;
    [SerializeField] private bool playOnStart;

    [Header("Playback")]
    [Tooltip("Optional. Leave empty to use or auto-create an AudioSource on this GameObject.")]
    public AudioSource audioSource;
    [Tooltip("Drag the preset audio clip here. The same clip will play for every speaker group.")]
    public AudioClip presetAudio;
    [Tooltip("Keep this enabled when the computer audio output feeds the amplifier.")]
    [SerializeField] private bool playPresetAudioOnComputer = true;
    [Tooltip("Speaker number groups in playback order. Speaker 1 maps to the first Arduino relay output, Speaker 20 maps to the last.")]
    [SerializeField]
    private SpeakerGroup[] speakerGroups =
    {
        new SpeakerGroup
        {
            name = "All 20",
            speakerNumbers = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }
        },
    };
    [Tooltip("0 means loop forever.")]
    [SerializeField, Min(0)] private int repeatCount = 1;
    [SerializeField, Min(0.01f)] private float speakerPlaySeconds = 0.5f;
    [SerializeField] private bool useAudioClipLength = true;
    [SerializeField, Min(0f)] private float gapSeconds = 0.15f;
    [Tooltip("Most Arduino boards reset when the serial port opens, so wait before the first command.")]
    [SerializeField, Min(0f)] private float startDelaySeconds = 2f;

    private object serialPort;
    private Type serialPortType;
    private MethodInfo openMethod;
    private MethodInfo closeMethod;
    private MethodInfo disposeMethod;
    private MethodInfo writeLineMethod;
    private PropertyInfo isOpenProperty;
    private Coroutine sequenceRoutine;

    public bool IsConnected
    {
        get
        {
            if (serialPort == null || isOpenProperty == null)
            {
                return false;
            }

            try
            {
                return (bool)isOpenProperty.GetValue(serialPort, null);
            }
            catch
            {
                return false;
            }
        }
    }

    private void Start()
    {
        if (connectOnStart)
        {
            Connect();
        }

        if (playOnStart)
        {
            PlaySequence();
        }
    }

    private void OnDestroy()
    {
        StopSequence();
        Disconnect();
    }

    public bool Connect()
    {
        if (IsConnected)
        {
            return true;
        }

        serialPortType = FindSerialPortType();
        if (serialPortType == null)
        {
            Debug.LogError("System.IO.Ports.SerialPort is not available. In Unity, set Player Settings > Other Settings > Api Compatibility Level to .NET Framework, then restart Unity.");
            return false;
        }

        try
        {
            CacheSerialMembers();
            serialPort = Activator.CreateInstance(serialPortType, portName, baudRate);
            SetSerialProperty("ReadTimeout", readTimeoutMs);
            SetSerialProperty("WriteTimeout", writeTimeoutMs);
            SetSerialProperty("NewLine", "\n");

            openMethod.Invoke(serialPort, null);
            Debug.Log("Arduino serial connected on " + portName + " at " + baudRate + " baud.");
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError("Could not connect to Arduino on " + portName + ": " + GetUsefulMessage(exception));
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        if (serialPort == null)
        {
            return;
        }

        try
        {
            if (IsConnected && closeMethod != null)
            {
                closeMethod.Invoke(serialPort, null);
            }

            if (disposeMethod != null)
            {
                disposeMethod.Invoke(serialPort, null);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Error while closing Arduino serial port: " + GetUsefulMessage(exception));
        }
        finally
        {
            serialPort = null;
        }
    }

    public void PlaySequence()
    {
        if (sequenceRoutine != null)
        {
            StopCoroutine(sequenceRoutine);
        }

        sequenceRoutine = StartCoroutine(PlaySequenceRoutine());
    }

    public void StopSequence()
    {
        if (sequenceRoutine != null)
        {
            StopCoroutine(sequenceRoutine);
            sequenceRoutine = null;
        }

        SendLine("STOP");
        StopPresetAudio();
    }

    public void SetSpeakerPins(params int[] speakerNumbers)
    {
        SetSpeakerGroups(new[] { speakerNumbers });
    }

    public void SetSpeakerGroups(params int[][] groups)
    {
        if (groups == null)
        {
            speakerGroups = new SpeakerGroup[0];
            return;
        }

        speakerGroups = new SpeakerGroup[groups.Length];
        for (int index = 0; index < groups.Length; index++)
        {
            int[] speakerNumbers = groups[index] == null ? new int[0] : (int[])groups[index].Clone();
            speakerGroups[index] = new SpeakerGroup
            {
                name = string.Join(",", Array.ConvertAll(speakerNumbers, speakerNumber => speakerNumber.ToString())),
                speakerNumbers = speakerNumbers,
            };
        }
    }

    public void PlaySingleSpeaker(int speakerNumber)
    {
        if (!IsConnected && !Connect())
        {
            return;
        }

        float durationSeconds = GetPlaybackDurationSeconds();
        SendGroupCommand(new[] { speakerNumber }, durationSeconds);
        PlayPresetAudioIfEnabled();
    }

    public void PlayAllSpeakers()
    {
        int[] allSpeakerNumbers = new int[20];
        for (int index = 0; index < allSpeakerNumbers.Length; index++)
        {
            allSpeakerNumbers[index] = index + 1;
        }

        if (!IsConnected && !Connect())
        {
            return;
        }

        float durationSeconds = GetPlaybackDurationSeconds();
        SendGroupCommand(allSpeakerNumbers, durationSeconds);
        PlayPresetAudioIfEnabled();
    }

    private IEnumerator PlaySequenceRoutine()
    {
        if (!IsConnected && !Connect())
        {
            sequenceRoutine = null;
            yield break;
        }

        if (speakerGroups == null || speakerGroups.Length == 0)
        {
            Debug.LogWarning("No speaker groups configured for ArduinoSpeakerSequencer.");
            sequenceRoutine = null;
            yield break;
        }

        if (startDelaySeconds > 0f)
        {
            yield return new WaitForSeconds(startDelaySeconds);
        }

        int loops = repeatCount == 0 ? int.MaxValue : repeatCount;
        for (int loop = 0; loop < loops; loop++)
        {
            for (int index = 0; index < speakerGroups.Length; index++)
            {
                SpeakerGroup speakerGroup = speakerGroups[index];
                if (speakerGroup == null || speakerGroup.speakerNumbers == null || speakerGroup.speakerNumbers.Length == 0)
                {
                    Debug.LogWarning("Skipping empty speaker group at index: " + index);
                    continue;
                }

                float durationSeconds = GetPlaybackDurationSeconds();
                SendGroupCommand(speakerGroup.speakerNumbers, durationSeconds);
                PlayPresetAudioIfEnabled();
                yield return new WaitForSeconds(durationSeconds + gapSeconds);
            }
        }

        sequenceRoutine = null;
    }

    private float GetPlaybackDurationSeconds()
    {
        if (useAudioClipLength && presetAudio != null)
        {
            return Mathf.Max(0.01f, presetAudio.length);
        }

        return Mathf.Max(0.01f, speakerPlaySeconds);
    }

    private void PlayPresetAudioIfEnabled()
    {
        if (!playPresetAudioOnComputer)
        {
            return;
        }

        PlayPresetAudio();
    }

    private void PlayPresetAudio()
    {
        if (presetAudio == null)
        {
            return;
        }

        AudioSource targetAudioSource = audioSource;
        if (targetAudioSource == null)
        {
            targetAudioSource = GetComponent<AudioSource>();
        }

        if (targetAudioSource == null)
        {
            targetAudioSource = gameObject.AddComponent<AudioSource>();
        }

        targetAudioSource.Stop();
        targetAudioSource.clip = presetAudio;
        targetAudioSource.Play();
    }

    private void StopPresetAudio()
    {
        AudioSource targetAudioSource = audioSource != null ? audioSource : GetComponent<AudioSource>();
        if (targetAudioSource != null)
        {
            targetAudioSource.Stop();
        }
    }

    private void SendGroupCommand(int[] speakerNumbers, float durationSeconds)
    {
        int durationMs = Mathf.Max(1, Mathf.RoundToInt(durationSeconds * 1000f));
        string command = "GROUP " + durationMs;

        for (int index = 0; index < speakerNumbers.Length; index++)
        {
            int speakerNumber = speakerNumbers[index];
            if (speakerNumber < 1 || speakerNumber > 20)
            {
                Debug.LogWarning("Skipping invalid speaker number: " + speakerNumber + ". Use 1-20.");
                continue;
            }

            command += " " + speakerNumber;
        }

        SendLine(command);
    }

    private void SendLine(string line)
    {
        if (!IsConnected || writeLineMethod == null)
        {
            return;
        }

        try
        {
            writeLineMethod.Invoke(serialPort, new object[] { line });
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not send Arduino command '" + line + "': " + GetUsefulMessage(exception));
        }
    }

    private void CacheSerialMembers()
    {
        openMethod = serialPortType.GetMethod("Open", Type.EmptyTypes);
        closeMethod = serialPortType.GetMethod("Close", Type.EmptyTypes);
        disposeMethod = serialPortType.GetMethod("Dispose", Type.EmptyTypes);
        writeLineMethod = serialPortType.GetMethod("WriteLine", new[] { typeof(string) });
        isOpenProperty = serialPortType.GetProperty("IsOpen");

        if (openMethod == null || writeLineMethod == null || isOpenProperty == null)
        {
            throw new MissingMethodException("SerialPort methods could not be found.");
        }
    }

    private void SetSerialProperty(string propertyName, object value)
    {
        PropertyInfo property = serialPortType.GetProperty(propertyName);
        if (property != null && property.CanWrite)
        {
            property.SetValue(serialPort, value, null);
        }
    }

    private static Type FindSerialPortType()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int index = 0; index < assemblies.Length; index++)
        {
            Type type = assemblies[index].GetType("System.IO.Ports.SerialPort");
            if (type != null)
            {
                return type;
            }
        }

        return Type.GetType("System.IO.Ports.SerialPort, System")
            ?? Type.GetType("System.IO.Ports.SerialPort, System.IO.Ports");
    }

    private static string GetUsefulMessage(Exception exception)
    {
        TargetInvocationException targetInvocationException = exception as TargetInvocationException;
        if (targetInvocationException != null && targetInvocationException.InnerException != null)
        {
            exception = targetInvocationException.InnerException;
        }

        return exception.GetType().Name + ": " + exception.Message;
    }
}
