import { guidedErrorFor } from '@/shared/awsGuided';
import type { IAwsProfileValidationResult } from '@/shared/types/IProject';

describe('guidedErrorFor', () => {
  it('returns null for a successful validation', () => {
    expect(guidedErrorFor({ ok: true, accountId: '123', arn: 'arn' })).toBeNull();
  });

  it('returns a preset for aws-cli-missing mentioning installation', () => {
    const err = guidedErrorFor({ ok: false, errorCode: 'aws-cli-missing' });
    expect(err?.title).toContain('AWS CLI');
    expect(err?.hint).toContain('winget');
  });

  it('returns a preset for expired-token mentioning IAM', () => {
    const err = guidedErrorFor({ ok: false, errorCode: 'expired-token' });
    expect(err?.title).toContain('expired');
    expect(err?.hint).toContain('IAM');
  });

  it('returns a preset for denied mentioning ECR permissions', () => {
    const err = guidedErrorFor({ ok: false, errorCode: 'denied' });
    expect(err?.hint).toContain('ecr:GetAuthorizationToken');
  });

  it('returns a preset for profile-not-found mentioning aws configure', () => {
    const err = guidedErrorFor({ ok: false, errorCode: 'profile-not-found' });
    expect(err?.hint).toContain('aws configure');
  });

  it('falls back to the server message and hint for unknown codes', () => {
    const err = guidedErrorFor({ ok: false, errorCode: null, message: 'Boom', hint: 'Retry later.' });
    expect(err?.title).toBe('Boom');
    expect(err?.hint).toBe('Retry later.');
  });
});