# DeyeSolar Mobile

Expo React Native client for Android and iOS.

## Run

```powershell
cd mobile
npm install
npm start
```

Default API URL:

- Android emulator: `http://10.0.2.2:5000`
- iOS simulator / local web: `http://localhost:5000`

For a physical phone, enter the LAN URL of the ASP.NET backend on the login screen.

## Backend API

The app uses bearer tokens from `POST /api/auth/login` and calls the new `/api/...` endpoints in `DeyeSolar.Web`.
