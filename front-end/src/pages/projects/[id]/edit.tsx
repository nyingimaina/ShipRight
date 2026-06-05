import { useEffect } from 'react';
import { useRouter } from 'next/router';

// Project editing is now a side pane on the Projects page
export default function EditProjectRedirect() {
  const router = useRouter();
  useEffect(() => { router.replace('/projects/'); }, []);
  return null;
}

export async function getStaticPaths() { return { paths: [], fallback: false }; }
export async function getStaticProps() { return { props: {} }; }
