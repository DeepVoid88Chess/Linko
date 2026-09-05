# Linko Windows agent

This folder will contain the Windows `Linko.exe` client.

The client is responsible for:

- displaying a pairing code
- connecting to the Linko Cloudflare WebSocket
- capturing the local screen
- sending compressed screen frames
- receiving mouse and keyboard commands

For safety, the production client must require explicit local approval before a remote session starts and must use authenticated, encrypted sessions. The first MVP can be developed against the relay using a test pairing code.

## Next implementation

Create a Windows .NET desktop/console client and publish it as a self-contained Windows executable. The client should use Windows `SendInput` for input and a Windows-supported screen capture API for frames.
