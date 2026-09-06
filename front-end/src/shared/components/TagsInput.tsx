import { useState } from 'react';

export interface TagsInputProps {
  value: string[];
  onChange: (tags: string[]) => void;
  allTags: string[];
  placeholder?: string;
  id?: string;
  style?: React.CSSProperties;
}

const chipStyle: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 4,
  background: 'rgba(201,168,76,0.15)',
  color: '#F0F2F5',
  border: '1px solid rgba(201,168,76,0.4)',
  borderRadius: 4,
  padding: '1px 4px',
  fontSize: 12,
};

const chipRemoveStyle: React.CSSProperties = {
  background: 'none',
  border: 'none',
  color: '#F0F2F5',
  cursor: 'pointer',
  padding: '0 2px',
  lineHeight: 1,
};

export default function TagsInput({
  value,
  onChange,
  allTags,
  placeholder = 'tag…',
  id,
  style,
}: TagsInputProps) {
  const [text, setText] = useState('');

  const addTag = (raw: string) => {
    const tag = raw.trim().replace(/,$/, '');
    if (!tag) return;
    if (!value.includes(tag)) onChange([...value, tag]);
    setText('');
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault();
      addTag(text);
    } else if (e.key === 'Backspace' && !text && value.length > 0) {
      onChange(value.slice(0, -1));
    }
  };

  const removeTag = (tag: string) => onChange(value.filter(t => t !== tag));

  return (
    <div
      style={{
        display: 'flex',
        flexWrap: 'wrap',
        gap: 4,
        alignItems: 'center',
        background: '#131D30',
        border: '1px solid rgba(255,255,255,0.12)',
        borderRadius: 6,
        padding: '4px 8px',
        boxSizing: 'border-box',
        ...style,
      }}
    >
      {value.map(tag => (
        <span key={tag} style={chipStyle}>
          {tag}
          <button
            type="button"
            style={chipRemoveStyle}
            onClick={() => removeTag(tag)}
            aria-label={`Remove tag ${tag}`}
          >
            ×
          </button>
        </span>
      ))}
      <input
        value={text}
        onChange={e => setText(e.target.value)}
        onKeyDown={handleKeyDown}
        onBlur={() => addTag(text)}
        list={id ? `${id}-datalist` : undefined}
        placeholder={value.length === 0 ? placeholder : ''}
        style={{
          flex: 1,
          minWidth: 80,
          background: 'transparent',
          border: 'none',
          outline: 'none',
          color: '#F0F2F5',
          fontSize: 13,
          padding: '4px 2px',
        }}
      />
      {allTags.length > 0 && id && (
        <datalist id={`${id}-datalist`}>
          {allTags.map(tag => <option key={tag} value={tag} />)}
        </datalist>
      )}
    </div>
  );
}