export {};
declare global {
  interface Window {
    diariz?: {
      isElectron?: boolean;
      startGoogleSignIn?: () => void;
      onAuthToken?: (cb: (token: string) => void) => () => void;
      onAuthError?: (cb: (reason: string) => void) => () => void;
    };
  }
}
