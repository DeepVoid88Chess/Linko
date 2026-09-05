# Linko

**Your computer. Wherever you are.**

Linko is a remote-computer project by **Donaro Inc.** It is designed to let you access and control a computer from an iPad through a web interface.

## MVP flow

1. Download and run `Linko.exe` on the computer.
2. Create or sign in to a Linko account.
3. Open the Linko website on the iPad.
4. Pair the iPad with the computer.
5. Start controlling the computer remotely.

## Planned controls

- Live computer screen
- Touch-to-click mouse control
- Dragging and scrolling
- Keyboard input
- Multiple paired computers
- Connection status
- Secure device pairing

## Architecture

The intended architecture separates the control plane from the screen/control data path:

```text
iPad browser <-> Linko web app <-> Cloudflare
                                      |
                                  signalling
                                      |
PC running Linko.exe <-------------> iPad
```

The MVP should prioritize a reliable, secure connection and low latency before adding advanced gaming controls.

## Project status

🚧 Early development — repository initialized.
