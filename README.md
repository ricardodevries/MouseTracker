## MouseTracker

MouseTracker is a .NET 8 application that I developed and use for my streams on Twitch that
tracks the mouse position using X11 on Linux. It accurately reports the mouse position relative
to the individual monitors based on my configuration set with xrandr. It then triggers an effect
filter on OBS Studio via websockets to move the camera away from the position my mouse is in.

This could probably be better programmed but who cares? It works for my needs! 🙂

### License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
