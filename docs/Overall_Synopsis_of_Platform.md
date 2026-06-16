**Diariz - A personal voice transcriber**

High level requirements:

- a multi platform app (Windows, MacOS, IOS, Android), starting on windows but using web technology that can be ported to other platforms , server side should be a .net app (C#), probably typescript for web front end 
- Records voice and transcribes into text
   - Using chosen microphone, or
   -  Using system audio (input/output) 
- These recordings are persisted firstly as audio files in a dedicated server in docker 
- Recordings are transcribed on the server into textual notes , ideally with speaker identification / marked as unknown speaker and tagged with the time (interval since start) that the voice occurred
-  Will summarise each recording using a server side local LLM  (OpenAI standard endpoint)
- Will offer chat style interaction with notes and across notes
- Needs to be multi user with login authentication / security and user-tied notes
- Recordings and associated notes can be downloaded
- Enable re-transcribe at will (to allow model change)





