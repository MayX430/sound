#include <stdlib.h>
#include <string.h>

const long BaudRate = 115200;
const size_t CommandBufferSize = 64;
const size_t MaxActivePins = 8;

char commandBuffer[CommandBufferSize];
size_t commandLength = 0;
int activePins[MaxActivePins];
size_t activePinCount = 0;
unsigned long activeStopAtMs = 0;

void setup()
{
    Serial.begin(BaudRate);
    Serial.println("READY");
}

void loop()
{
    while (Serial.available() > 0)
    {
        char character = (char)Serial.read();
        if (character == '\n' || character == '\r')
        {
            if (commandLength > 0)
            {
                commandBuffer[commandLength] = '\0';
                handleCommand(commandBuffer);
                commandLength = 0;
            }
        }
        else if (commandLength < CommandBufferSize - 1)
        {
            commandBuffer[commandLength] = character;
            commandLength++;
        }
        else
        {
            commandLength = 0;
            Serial.println("ERR OVERFLOW");
        }
    }

    updateActiveSpeaker();
}

void handleCommand(char *line)
{
    char *command = strtok(line, " ");
    if (command == NULL)
    {
        return;
    }

    if (strcmp(command, "PING") == 0)
    {
        Serial.println("PONG");
        return;
    }

    if (strcmp(command, "STOP") == 0)
    {
        stopActiveSpeaker();
        Serial.println("OK STOP");
        return;
    }

    if (strcmp(command, "GROUP") != 0)
    {
        Serial.println("ERR UNKNOWN");
        return;
    }

    char *durationToken = strtok(NULL, " ");
    if (durationToken == NULL)
    {
        Serial.println("ERR FORMAT");
        return;
    }

    unsigned long durationMs = strtoul(durationToken, NULL, 10);
    if (durationMs == 0)
    {
        Serial.println("ERR VALUE");
        return;
    }

    int pins[MaxActivePins];
    size_t pinCount = 0;
    char *pinToken = strtok(NULL, " ");
    while (pinToken != NULL && pinCount < MaxActivePins)
    {
        int speakerPin = atoi(pinToken);
        if (speakerPin < 0)
        {
            Serial.println("ERR VALUE");
            return;
        }

        pins[pinCount] = speakerPin;
        pinCount++;
        pinToken = strtok(NULL, " ");
    }

    if (pinCount == 0)
    {
        Serial.println("ERR FORMAT");
        return;
    }

    playSpeakerGroup(pins, pinCount, durationMs);
    Serial.print("OK GROUP ");
    Serial.println(pinCount);
}

void playSpeakerGroup(int pins[], size_t pinCount, unsigned long durationMs)
{
    stopActiveSpeaker();

    for (size_t index = 0; index < pinCount; index++)
    {
        int speakerPin = pins[index];
        pinMode(speakerPin, OUTPUT);
        digitalWrite(speakerPin, HIGH);
        activePins[index] = speakerPin;
    }

    activePinCount = pinCount;
    activeStopAtMs = millis() + durationMs;
}

void updateActiveSpeaker()
{
    if (activePinCount > 0 && (long)(millis() - activeStopAtMs) >= 0)
    {
        stopActiveSpeaker();
    }
}

void stopActiveSpeaker()
{
    for (size_t index = 0; index < activePinCount; index++)
    {
        digitalWrite(activePins[index], LOW);
    }

    activePinCount = 0;
    activeStopAtMs = 0;
}
