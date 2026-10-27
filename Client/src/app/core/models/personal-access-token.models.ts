export interface PersonalAccessTokenDto {
  id: string;
  name: string;
  tokenPrefix: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
  isActive: boolean;
  isRevoked: boolean;
  isExpired: boolean;
}

export interface CreatedPersonalAccessTokenDto {
  token: PersonalAccessTokenDto;
  plaintextToken: string;
}

export interface CreatePersonalAccessTokenRequest {
  name: string;
  expiresAt?: string | null;
}
