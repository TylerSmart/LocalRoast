export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  email: string;
  username: string;
  password: string;
}

export interface AuthResponse {
  token: string;
  id: string;
  email: string;
  username: string;
  expiresAt: string | null;
  themeSettings?: string | null;
}

export interface CurrentUserDto {
  id: string;
  email: string;
  username: string;
  themeSettings?: string | null;
}

export interface OidcConfigurationDto {
  enabled: boolean;
  providerName: string;
  authority?: string;
  clientId?: string;
  scope?: string;
  authorizationEndpoint?: string;
}

export interface AuthProviderDto {
  id: string;
  name: string;
  isExternal: boolean;
  oidcConfig?: OidcConfigurationDto;
  authorizationUrl?: string;
}

export interface ExternalAuthRequest {
  code?: string | null;
  idToken?: string | null;
  accessToken?: string | null;
  redirectUri?: string | null;
}
