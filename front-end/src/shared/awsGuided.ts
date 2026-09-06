import type { IAwsProfileValidationResult } from '@/shared/types/IProject';

export interface GuidedError {
  title: string;
  hint: string;
}

const message: Record<string, { title: string; hint: string }> = {
  'aws-cli-missing': {
    title: 'The AWS CLI is not installed on the build machine.',
    hint: 'Install AWS CLI v2 where builds run: on Windows run "winget install Amazon.AWSCLI", in WSL/Linux run "sudo apt install awscli". Then refresh and test again.',
  },
  'profile-not-found': {
    title: 'The named AWS profile is not present on the build machine.',
    hint: 'Run "aws configure --profile <name>" on the build machine, or paste explicit access keys here instead of using a named profile.',
  },
  'invalid-credentials': {
    title: 'The AWS credentials were rejected.',
    hint: 'The access key is invalid, expired, or no longer exists. Create a fresh key in IAM (Users → Security credentials → Create access key) and update this profile.',
  },
  'expired-token': {
    title: 'The AWS credentials are expired.',
    hint: 'Create a fresh access key in the AWS IAM console (Users → Security credentials → Create access key) and update this profile with it.',
  },
  denied: {
    title: 'The credentials work, but lack permission to access ECR.',
    hint: 'Attach a policy with "ecr:GetAuthorizationToken" (and push/pull) to the IAM user or role this profile uses.',
  },
  network: {
    title: 'Could not reach AWS or resolve credentials.',
    hint: 'Check the build machine can reach the internet, and that credentials exist in ~/.aws/credentials there. Then retry.',
  },
  'no-region': {
    title: 'No AWS region is set.',
    hint: 'Set a default region on this profile (e.g. us-east-1) — it drives which ECR endpoint the token is fetched from.',
  },
  'empty-output': {
    title: 'AWS returned an unexpected response.',
    hint: 'Check that AWS CLI v2 is installed on the build machine and reachable, then retry.',
  },
};

export function guidedErrorFor(result: IAwsProfileValidationResult): GuidedError | null {
  if (result.ok) return null;
  const preset = result.errorCode ? message[result.errorCode] : undefined;
  if (preset) return preset;
  return {
    title: result.message || 'Connection test failed.',
    hint: result.hint || '',
  };
}