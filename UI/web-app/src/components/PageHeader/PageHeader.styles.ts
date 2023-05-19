// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

import { IPageHeaderStyleProps, IPageHeaderStyles } from './PageHeader.types';

export const getStyles = (props: IPageHeaderStyleProps): IPageHeaderStyles => {
  const { className, theme } = props;

  return {
    root: [
      {
        
      },
      className
    ]
  };
};
