import { render, screen, fireEvent } from '@testing-library/react';
import TagsInput from '../TagsInput';

describe('TagsInput', () => {
  it('renders existing tags as chips with remove buttons', () => {
    render(<TagsInput value={['ecr', 'prod']} onChange={jest.fn()} allTags={[]} />);
    expect(screen.getByText('ecr')).toBeInTheDocument();
    expect(screen.getByText('prod')).toBeInTheDocument();
    expect(screen.getByLabelText('Remove tag ecr')).toBeInTheDocument();
  });

  it('adds a tag on Enter', () => {
    const onChange = jest.fn();
    render(<TagsInput value={[]} onChange={onChange} allTags={[]} />);
    fireEvent.keyDown(screen.getByPlaceholderText('tag…'), { key: 'Enter' });
    fireEvent.change(screen.getByPlaceholderText('tag…'), { target: { value: 'dev' } });
    fireEvent.keyDown(screen.getByPlaceholderText('tag…'), { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith(['dev']);
  });

  it('adds a tag on comma including the comma', () => {
    const onChange = jest.fn();
    render(<TagsInput value={[]} onChange={onChange} allTags={[]} />);
    fireEvent.change(screen.getByPlaceholderText('tag…'), { target: { value: 'prod,' } });
    fireEvent.keyDown(screen.getByPlaceholderText('tag…'), { key: ',' });
    expect(onChange).toHaveBeenCalledWith(['prod']);
  });

  it('does not duplicate tags', () => {
    const onChange = jest.fn();
    render(<TagsInput value={['dev']} onChange={onChange} allTags={[]} />);
    fireEvent.change(screen.getByPlaceholderText(''), { target: { value: 'dev' } });
    fireEvent.keyDown(screen.getByPlaceholderText(''), { key: 'Enter' });
    expect(onChange).not.toHaveBeenCalled();
  });

  it('removes the last tag on Backspace when input is empty', () => {
    const onChange = jest.fn();
    render(<TagsInput value={['ecr', 'prod']} onChange={onChange} allTags={[]} />);
    fireEvent.keyDown(screen.getByPlaceholderText(''), { key: 'Backspace' });
    expect(onChange).toHaveBeenCalledWith(['ecr']);
  });

  it('removes a tag when its x button is clicked', () => {
    const onChange = jest.fn();
    render(<TagsInput value={['ecr', 'prod']} onChange={onChange} allTags={[]} />);
    fireEvent.click(screen.getByLabelText('Remove tag ecr'));
    expect(onChange).toHaveBeenCalledWith(['prod']);
  });

  it('adds current text on blur', () => {
    const onChange = jest.fn();
    render(<TagsInput value={[]} onChange={onChange} allTags={[]} />);
    fireEvent.change(screen.getByPlaceholderText('tag…'), { target: { value: 'aws' } });
    fireEvent.blur(screen.getByPlaceholderText('tag…'));
    expect(onChange).toHaveBeenCalledWith(['aws']);
  });

  it('renders datalist suggestions from allTags', () => {
    render(<TagsInput value={[]} onChange={jest.fn()} allTags={['ecr', 'github']} id="reg-tags" />);
    const datalist = document.getElementById('reg-tags-datalist');
    expect(datalist).toBeInTheDocument();
    expect(datalist!.querySelectorAll('option').length).toBe(2);
  });
});