import { useEffect } from 'react';
import { useRouter } from 'next/router';

export default function ProjectDetailRedirect() {
  const router = useRouter();
  const { id } = router.query;
  useEffect(() => {
    if (id) router.replace(`/projects/?detail=${id}`);
  }, [id]);
  return null;
}

export async function getStaticPaths() { return { paths: [], fallback: false }; }
export async function getStaticProps() { return { props: {} }; }