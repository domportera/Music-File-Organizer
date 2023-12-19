# Music File Organizer

**IMPORTANT: MAKE A BACKUP BEFORE RUNNING THIS APPLICATION!** There is no warranty etc etc, and it's a bit under-tested.

This CLI application organizes all of your music files in your music directory according to the tag data on those files. 

## Features:
- Sorts files into Artist/Year - Album/##. TrackName
- Sorts playlists into /Playlists folder
- If duplicates are found, the file with the highest bitrate will be preserved. 
- It will also attempt to moving any album art along with it, but no promises!

Simply run the application with an argument for your music directory. e.g. `music-rename.exe "C:/User/YourName/Music"`

No builds are currently available, however it should be simple to run this from within Visual Studio or Rider.
